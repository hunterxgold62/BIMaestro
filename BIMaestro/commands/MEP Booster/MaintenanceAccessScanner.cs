using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    // Scans static conservative volumes, once per nearby source element. Unlike
    // the motor extraction scanner, this does not search a first-contact distance
    // (each box already represents an entire continuous operation interval).
    internal static class MaintenanceAccessScanner
    {
        private sealed class Volume : IDisposable
        {
            internal MaintenanceAccessBox Box;
            internal XYZ Lo, Hi;
            private Solid[] _parts;
            internal Solid[] Parts => _parts ?? (_parts = Box.CollisionParts().ToArray());
            public void Dispose() { if(_parts!=null)foreach(var part in _parts)part.Dispose(); }
        }

        internal static MaintenanceAccessReport Run(Document doc, FamilyInstance source, MaintenanceAccessScene scene, Action<string> progress = null, Func<bool> canceled = null)
        {
            if (doc == null || source == null || scene == null) throw new ArgumentNullException("Le document, l’équipement et la scène sont nécessaires.");
            var report = new MaintenanceAccessReport();
            var callbacks = new MaintenanceAccessCallbackGuard();
            var clock = Stopwatch.StartNew();
            long lastProgress = 0;
            int visited = 0, intersections = 0;
            void CheckProgress(bool force = false)
            {
                callbacks.CheckCanceled(canceled);
                if (!force && clock.ElapsedMilliseconds - lastProgress < 150) return;
                lastProgress = clock.ElapsedMilliseconds;
                callbacks.Report(progress, "Contrôle : " + visited + " éléments, " + intersections + " intersections testées…");
            }
            CheckProgress();
            var sourceIds = new HashSet<long>();
            void AddSource(FamilyInstance item)
            {
                if (!sourceIds.Add(BoosterIds.Value(item.Id))) return;
                foreach (ElementId id in item.GetSubComponentIds()) if (doc.GetElement(id) is FamilyInstance child) AddSource(child);
            }
            AddSource(source);
            var volumes = scene.CollisionBoxes.Select(box =>
            {
                XYZ[] points = box.BoundsCorners();
                return new Volume { Box = box, Lo = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                    Hi = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)) };
            }).ToArray();
            report.CheckedVolumes = volumes.Length;
            if (volumes.Length == 0) return report;
            using (var worldBounds = MotorEnvelope.Bounds(volumes.SelectMany(v => new[] { v.Lo, v.Hi })))
            try
            {
                Scan(doc, Transform.Identity, null, true);
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    Document linked = link.GetLinkDocument();
                    if (linked == null) { report.Unknown.Add("Lien non chargé : " + link.Name); continue; }
                    Scan(linked, link.GetTotalTransform(), link.Name, false, BoosterIds.Value(link.Id));
                }
                void Scan(Document model, Transform transform, string linkName, bool host, long linkId=-1)
                {
                    string prefix = host ? "" : linkName + " / ";
                    callbacks.Report(progress, host ? "Contrôle du modèle actif…" : "Contrôle du lien " + linkName + "…");
                    XYZ lo = worldBounds.MinimumPoint, hi = worldBounds.MaximumPoint;
                    var worldCorners = Enumerable.Range(0, 8).Select(i => new XYZ((i & 1) == 0 ? lo.X : hi.X,
                        (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z));
                    using (var localBounds = MotorEnvelope.Bounds(worldCorners.Select(transform.Inverse.OfPoint)))
                    using (var filter = new BoundingBoxIntersectsFilter(localBounds))
                    using (var collector = new FilteredElementCollector(model).WhereElementIsNotElementType().WherePasses(filter))
                    foreach (Element element in collector)
                    {
                        visited++;
                        CheckProgress(visited % 10 == 0);
                        if (element is RevitLinkInstance)
                        {
                            if (!host) report.Unknown.Add(prefix + element.Name + " : lien imbriqué non contrôlé");
                            continue;
                        }
                        if (MaintenanceOperatorPlacement.IsReferenceElement(element) || element.Category == null || element.Category.CategoryType != CategoryType.Model) continue;
                        long id = BoosterIds.Value(element.Id);
                        bool selectedBody = host && sourceIds.Contains(id);
                        string label = prefix + element.Name + " [" + id + "]";
                        var solids = new List<Solid>();
                        try
                        {
                            XYZ[] points = BoundingPoints(element, transform);
                            if (points == null) continue;
                            XYZ elementLo = new XYZ(points.Min(q => q.X), points.Min(q => q.Y), points.Min(q => q.Z));
                            XYZ elementHi = new XYZ(points.Max(q => q.X), points.Max(q => q.Y), points.Max(q => q.Z));
                            Volume[] candidates = volumes.Where(v => (!selectedBody || v.Box.CheckSource) && Overlap(v.Lo, v.Hi, elementLo, elementHi)).ToArray();
                            if (candidates.Length == 0) continue;
                            if(element is ImportInstance && MaintenanceOperatorPlacement.IsPlanarReferenceImport(element,transform)) continue;
                            bool incomplete = false;
                            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
                            using (var geometry = element.get_Geometry(options))
                            {
                                if (geometry != null) Read(geometry);
                                void Read(GeometryElement shape)
                                {
                                    if(shape==null){incomplete=true;return;}
                                    foreach (GeometryObject item in shape)
                                    {
                                        if (item is GeometryInstance nested)
                                        { using (var expanded = nested.GetInstanceGeometry()) Read(expanded); }
                                        else if (item is Solid solid && solid.Volume > 1e-12)
                                            solids.Add(SolidUtils.CreateTransformed(solid, transform));
                                        else if (item is Mesh mesh && mesh.NumTriangles > 0) incomplete = true;
                                    }
                                }
                            }
                            if (solids.Count == 0 || incomplete) report.Unknown.Add(label + " : géométrie solide incomplète");
                            // One hit per element and functional volume label is
                            // enough; hundreds of animation intervals are grouped.
                            foreach (var group in candidates.GroupBy(v => new { v.Box.Role, v.Box.Label }))
                            {
                                bool found = false;
                                foreach (Volume volume in group)
                                {
                                    CheckProgress();
                                    foreach (Solid part in volume.Parts)
                                    {
                                        foreach (Solid solid in solids)
                                        using (var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(part, solid, BooleanOperationsType.Intersect))
                                        {
                                            intersections++;
                                            CheckProgress();
                                            if (intersection.Volume <= 1e-10) continue;
                                            found = true;
                                            break;
                                        }
                                        if(found)break;
                                    }
                                    if (found) break;
                                }
                                if (found) report.Obstacles.Add(new MaintenanceAccessObstacle { Label = label, ElementId = id, LinkName = linkName,LinkId=linkId,
                                    Role = group.Key.Role, VolumeLabel = group.Key.Label, Box = points });
                            }
                        }
                        catch (Exception ex) when (!callbacks.IsCallbackFailure(ex)
                            && (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException))
                        { report.Unknown.Add(label + " : contrôle géométrique impossible (" + ex.GetType().Name + ")"); }
                        finally { foreach (Solid solid in solids) solid.Dispose(); }
                    }
                }
            }
            finally { foreach (Volume volume in volumes) volume.Dispose(); }
            return report;
        }

        private static bool Overlap(XYZ lo, XYZ hi, XYZ otherLo, XYZ otherHi)
            => lo.X < otherHi.X && hi.X > otherLo.X && lo.Y < otherHi.Y && hi.Y > otherLo.Y && lo.Z < otherHi.Z && hi.Z > otherLo.Z;

        private static XYZ[] BoundingPoints(Element element, Transform transform)
        {
            using (BoundingBoxXYZ box = element.get_BoundingBox(null))
            {
                if (box == null) return null;
                return Enumerable.Range(0, 8).Select(i => transform.OfPoint(box.Transform.OfPoint(new XYZ(
                    (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                    (i & 4) == 0 ? box.Min.Z : box.Max.Z)))).ToArray();
            }
        }
    }
}

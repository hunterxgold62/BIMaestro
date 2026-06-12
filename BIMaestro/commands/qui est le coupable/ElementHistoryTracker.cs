using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Analyse
{
    internal sealed class ElementHistoryEvent
    {
        public DateTime Ts { get; set; }
        public string Project { get; set; }
        public string ModelGuid { get; set; }
        public string ModelKey { get; set; }
        public int ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string TypeName { get; set; }
        public string Action { get; set; }
        public string User { get; set; }
        public string Tx { get; set; }
        public Dictionary<string, object> Delta { get; set; }
    }

    internal static class ElementHistoryTracker
    {
        private sealed class ElementSnapshot
        {
            public string UniqueId { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string TypeName { get; set; }
            public string Name { get; set; }
            public XYZ Location { get; set; }
            public XYZ BBoxMin { get; set; }
            public XYZ BBoxMax { get; set; }
            public List<XYZ> ObbCorners { get; set; }
            public DateTime LastLogged { get; set; }
        }

        private static readonly ConcurrentQueue<ElementHistoryEvent> Queue = new ConcurrentQueue<ElementHistoryEvent>();
        private static readonly object StartSync = new object();
        private static readonly object FileSync = new object();
        private static readonly Dictionary<int, ElementSnapshot> SnapshotByElementId = new Dictionary<int, ElementSnapshot>();
        private static readonly HashSet<string> PrimedDocumentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static CancellationTokenSource _cts;
        private static Task _worker;
        private const double MinMoveFeet = 0.00656168; // ~2 mm
        private const bool IncludeAnnotationCategoriesForFuture = false;
        private static readonly string[] IgnoredTransactionFragments =
        {
            "BIMaestro - Visualisation historique",
            "BIMaestro - Visualisation suppression",
            "BIMaestro - Nettoyer previews historique"
        };


        public static void Start()
        {
            lock (StartSync)
            {
                if (_worker != null) return;
                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => WorkerLoop(_cts.Token));
            }
        }

        public static void Stop()
        {
            lock (StartSync)
            {
                if (_worker == null) return;
                _cts.Cancel();
                try { _worker.Wait(1500); } catch { }
                Flush();
                _worker = null;
            }
        }

        public static void PrimeDocument(Document doc)
        {
            if (doc == null) return;

            var key = GetDocumentKey(doc);
            lock (SnapshotByElementId)
            {
                if (PrimedDocumentKeys.Contains(key)) return;
                PrimedDocumentKeys.Add(key);
            }

            try
            {
                foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    try
                    {
                        if (el == null || ShouldIgnoreElement(el)) continue;
                        var snapshot = BuildSnapshot(el, includeOrientedCorners: false);
                        snapshot.LastLogged = DateTime.UtcNow;
                        lock (SnapshotByElementId)
                        {
                            SnapshotByElementId[el.Id.IntegerValue] = snapshot;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public static void CaptureDocumentChanges(Document doc, DocumentChangedEventArgs e)
        {
            if (doc == null || e == null) return;
            var user = Environment.UserName;
            var tx = e.GetTransactionNames()?.FirstOrDefault() ?? "Transaction";
            if (IsIgnoredTransaction(tx)) return;
            PrimeDocument(doc);

            foreach (var id in e.GetAddedElementIds())
                CaptureAddedOrModified(doc, id, user, tx, isCreate: true);
            foreach (var id in e.GetModifiedElementIds())
                CaptureAddedOrModified(doc, id, user, tx, isCreate: false);
            foreach (var id in e.GetDeletedElementIds())
                EnqueueDeleted(doc, id, user, tx);
        }

        public static List<ElementHistoryEvent> LoadElementHistory(Document doc, Element element)
        {
            var uniqueId = element?.UniqueId;
            if (string.IsNullOrWhiteSpace(uniqueId)) return new List<ElementHistoryEvent>();
            var key = GetDocumentKey(doc);
            return LoadAllHistory(doc)
                .Where(ev => string.Equals(ev.ModelKey ?? string.Empty, key, StringComparison.OrdinalIgnoreCase))
                .Where(ev => string.Equals(ev.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Ts)
                .Take(300)
                .ToList();
        }


        public static List<ElementHistoryEvent> LoadRecentModelHistory(Document doc, int take = 400)
        {
            var key = GetDocumentKey(doc);
            return LoadAllHistory(doc)
                .Where(ev => string.Equals(ev.ModelKey ?? string.Empty, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Ts)
                .Take(Math.Max(1, take))
                .ToList();
        }

        public static List<ElementHistoryEvent> LoadRecentDeletedHistory(Document doc)
        {
            var key = GetDocumentKey(doc);
            return LoadAllHistory(doc)
                .Where(ev => string.Equals(ev.ModelKey ?? string.Empty, key, StringComparison.OrdinalIgnoreCase))
                .Where(ev => string.Equals(ev.Action, "delete", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Ts)
                .Take(400)
                .ToList();
        }

        internal static bool IsDisplayableHistoryEvent(ElementHistoryEvent ev)
        {
            if (ev == null) return false;
            return IsUsefulHistoryText(ev.Category);
        }

        private static List<ElementHistoryEvent> LoadAllHistory(Document doc)
        {
            var files = Directory.Exists(CollaborativeModelTrackerStore.ActiveDirectory)
                ? Directory.GetFiles(CollaborativeModelTrackerStore.ActiveDirectory, "element-history-*.jsonl")
                : Array.Empty<string>();
            var result = new List<ElementHistoryEvent>();
            foreach (var f in files)
            {
                foreach (var line in File.ReadLines(f))
                {
                    try
                    {
                        var ev = JsonConvert.DeserializeObject<ElementHistoryEvent>(line);
                        if (ev != null) result.Add(ev);
                    }
                    catch { }
                }
            }
            return result;
        }

        private static void CaptureAddedOrModified(Document doc, ElementId id, string user, string tx, bool isCreate)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            var el = doc.GetElement(id);
            if (el == null || ShouldIgnoreElement(el)) return;

            var current = BuildSnapshot(el, includeOrientedCorners: true);
            ElementSnapshot previous;
            lock (SnapshotByElementId)
            {
                SnapshotByElementId.TryGetValue(id.IntegerValue, out previous);
            }

            var action = isCreate ? "create" : DetermineAction(previous, current);
            if (!isCreate && action == "modify_skip") return;

            var delta = BuildDelta(action, previous, current);

            Queue.Enqueue(new ElementHistoryEvent
            {
                Ts = DateTime.UtcNow,
                Project = doc.ProjectInformation?.Name ?? "Projet",
                ModelGuid = doc.GetHashCode().ToString(CultureInfo.InvariantCulture),
                ModelKey = GetDocumentKey(doc),
                ElementId = id.IntegerValue,
                UniqueId = current.UniqueId,
                Category = current.Category,
                Family = current.Family,
                TypeName = current.TypeName,
                Action = action,
                User = user,
                Tx = tx,
                Delta = delta
            });

            current.LastLogged = DateTime.UtcNow;
            lock (SnapshotByElementId)
            {
                SnapshotByElementId[id.IntegerValue] = current;
            }
        }

        private static ElementSnapshot BuildSnapshot(Element el, bool includeOrientedCorners)
        {
            string family = null;
            string typeName = null;
            var categoryName = CleanHistoryText(el.Category?.Name);
            try
            {
                var type = el.Document.GetElement(el.GetTypeId()) as ElementType;
                typeName = type?.Name;
                if (type is FamilySymbol fs) family = fs.FamilyName;
            }
            catch { }

            return new ElementSnapshot
            {
                UniqueId = el.UniqueId,
                Category = categoryName,
                Family = CleanHistoryText(family),
                TypeName = CleanHistoryText(typeName),
                Name = el.Name ?? string.Empty,
                Location = GetLocation(el),
                BBoxMin = GetBBoxMin(el),
                BBoxMax = GetBBoxMax(el),
                ObbCorners = includeOrientedCorners ? GetOrientedCorners(el) : null
            };
        }

        private static string DetermineAction(ElementSnapshot previous, ElementSnapshot current)
        {
            if (previous == null) return "modify";
            if (!string.Equals(previous.TypeName, current.TypeName, StringComparison.OrdinalIgnoreCase)) return "type_change";
            if (HasMoved(previous.Location, current.Location)) return "move";

            var now = DateTime.UtcNow;
            if ((now - previous.LastLogged).TotalSeconds < 3) return "modify_skip";
            return "param_change";
        }

        private static Dictionary<string, object> BuildDelta(string action, ElementSnapshot previous, ElementSnapshot current)
        {
            if (action == "move" && previous?.Location != null && current?.Location != null)
            {
                return new Dictionary<string, object>
                {
                    ["old"] = new { x = previous.Location.X, y = previous.Location.Y, z = previous.Location.Z },
                    ["new"] = new { x = current.Location.X, y = current.Location.Y, z = current.Location.Z },
                    ["dx"] = current.Location.X - previous.Location.X,
                    ["dy"] = current.Location.Y - previous.Location.Y,
                    ["dz"] = current.Location.Z - previous.Location.Z
                };
            }

            if (action == "type_change")
            {
                return new Dictionary<string, object>
                {
                    ["oldType"] = previous?.TypeName ?? "",
                    ["newType"] = current?.TypeName ?? ""
                };
            }
            return null;
        }

        private static void EnqueueDeleted(Document doc, ElementId id, string user, string tx)
        {
            if (id == null || id == ElementId.InvalidElementId) return;

            ElementSnapshot snapshot = null;
            lock (SnapshotByElementId)
            {
                if (id != null) SnapshotByElementId.TryGetValue(id.IntegerValue, out snapshot);
                if (id != null) SnapshotByElementId.Remove(id.IntegerValue);
            }

            if (ShouldIgnoreSnapshot(snapshot)) return;

            Queue.Enqueue(new ElementHistoryEvent
            {
                Ts = DateTime.UtcNow,
                Project = doc.ProjectInformation?.Name ?? "Projet",
                ModelGuid = doc.GetHashCode().ToString(CultureInfo.InvariantCulture),
                ModelKey = GetDocumentKey(doc),
                ElementId = id.IntegerValue,
                UniqueId = "deleted:" + id.IntegerValue,
                Category = snapshot.Category,
                Family = snapshot?.Family ?? string.Empty,
                TypeName = snapshot?.TypeName ?? string.Empty,
                Action = "delete",
                User = user,
                Tx = tx,
                Delta = BuildDeleteDelta(snapshot)
            });
        }




        private static Dictionary<string, object> BuildDeleteDelta(ElementSnapshot snapshot)
        {
            if (snapshot == null) return null;

            var delta = new Dictionary<string, object>
            {
                ["deletedUniqueId"] = snapshot.UniqueId,
                ["lastKnown"] = snapshot.Location == null ? null : new { x = snapshot.Location.X, y = snapshot.Location.Y, z = snapshot.Location.Z },
                ["bboxMin"] = snapshot.BBoxMin == null ? null : new { x = snapshot.BBoxMin.X, y = snapshot.BBoxMin.Y, z = snapshot.BBoxMin.Z },
                ["bboxMax"] = snapshot.BBoxMax == null ? null : new { x = snapshot.BBoxMax.X, y = snapshot.BBoxMax.Y, z = snapshot.BBoxMax.Z },
                ["obbCorners"] = snapshot.ObbCorners == null ? null : snapshot.ObbCorners.Select(pt => new { x = pt.X, y = pt.Y, z = pt.Z }).ToArray()
            };

            return delta.Values.Any(v => v != null) ? delta : null;
        }

        private static bool IsIgnoredTransaction(string transactionName)
        {
            if (string.IsNullOrWhiteSpace(transactionName)) return false;
            return IgnoredTransactionFragments.Any(fragment =>
                transactionName.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ShouldIgnoreElement(Element element)
        {
            if (element == null) return true;
            if (IsBIMaestroPreviewElement(element)) return true;
            if (ShouldIgnoreCategory(element.Category)) return true;
            return false;
        }

        private static bool ShouldIgnoreSnapshot(ElementSnapshot snapshot)
        {
            if (snapshot == null) return true;
            if (!string.IsNullOrWhiteSpace(snapshot.Name) &&
                snapshot.Name.StartsWith("BIMaestro_Preview_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!IsUsefulHistoryText(snapshot.Category)) return true;
            if (IsIgnoredCategoryName(snapshot.Category))
                return true;
            return false;
        }

        private static bool IsBIMaestroPreviewElement(Element element)
        {
            if (!(element is DirectShape)) return false;
            var name = element.Name ?? string.Empty;
            return name.StartsWith("BIMaestro_Preview_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("BIMaestro_DeletedPreview_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldIgnoreCategory(Category category)
        {
            if (category == null) return true;
            if (IsIgnoredCategoryName(category.Name)) return true;

            try
            {
                if (!IncludeAnnotationCategoriesForFuture && category.CategoryType == CategoryType.Annotation) return true;
            }
            catch
            {
            }
            return false;
        }

        private static bool IsIgnoredCategoryName(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return true;
            var name = categoryName.Trim();
            return name.Equals("Views", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Sheets", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Schedules", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Viewports", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Materials", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Line Styles", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Project Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RVT Links", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CAD Links", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanHistoryText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.Trim();
            return text.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? string.Empty : text;
        }

        private static bool IsUsefulHistoryText(string value)
        {
            var text = CleanHistoryText(value);
            return !string.IsNullOrWhiteSpace(text)
                && !text.Equals("-", StringComparison.OrdinalIgnoreCase)
                && !text.Equals("?", StringComparison.OrdinalIgnoreCase)
                && !text.Equals("N/A", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDocumentKey(Document doc)
        {
            if (doc == null) return string.Empty;
            try
            {
                string central = null;
                if (doc.IsWorkshared)
                {
                    var mp = doc.GetWorksharingCentralModelPath();
                    if (mp != null) central = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                }
                return (central ?? doc.PathName ?? doc.Title ?? string.Empty).Trim();
            }
            catch { return (doc.PathName ?? doc.Title ?? string.Empty).Trim(); }
        }

        private static List<XYZ> GetOrientedCorners(Element el)
        {
            try
            {
                var opts = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                var ge = el.get_Geometry(opts);
                var pts = new List<XYZ>();
                CollectPoints(ge, pts);
                if (pts.Count < 8) return null;

                double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y), cz = pts.Average(p => p.Z);
                XYZ c = new XYZ(cx, cy, cz);
                XYZ v1 = Normalize(new XYZ(1, 0, 0));
                XYZ v2 = Normalize(new XYZ(0, 1, 0));
                XYZ v3 = Normalize(v1.CrossProduct(v2));

                // lightweight PCA-ish refinement
                for (int i = 0; i < 10; i++)
                {
                    v1 = Normalize(ApplyCov(pts, c, v1));
                    v2 = Normalize(ApplyCov(pts, c, v2));
                    v2 = Normalize(Sub(v2, Scale(v1, v1.DotProduct(v2))));
                    v3 = Normalize(v1.CrossProduct(v2));
                }

                double min1 = double.MaxValue, min2 = double.MaxValue, min3 = double.MaxValue;
                double max1 = double.MinValue, max2 = double.MinValue, max3 = double.MinValue;
                foreach (var p in pts)
                {
                    var d = p - c;
                    var a = d.DotProduct(v1); var b = d.DotProduct(v2); var g = d.DotProduct(v3);
                    if (a < min1) min1 = a; if (a > max1) max1 = a;
                    if (b < min2) min2 = b; if (b > max2) max2 = b;
                    if (g < min3) min3 = g; if (g > max3) max3 = g;
                }

                var corners = new List<XYZ>(8);
                double[] A = { min1, max1 }, B = { min2, max2 }, G = { min3, max3 };
                foreach (var a in A)
                    foreach (var b in B)
                        foreach (var g in G)
                            corners.Add(Add(Add(Add(c, Scale(v1, a)), Scale(v2, b)), Scale(v3, g)));
                return corners;
            }
            catch { return null; }
        }

        private static void CollectPoints(GeometryElement ge, List<XYZ> pts)
        {
            if (ge == null) return;
            foreach (var go in ge)
            {
                if (go is Solid s && s.Faces.Size > 0)
                {
                    foreach (Face f in s.Faces)
                    {
                        var m = f.Triangulate();
                        for (int i = 0; i < m.Vertices.Count; i++) pts.Add(m.Vertices[i]);
                    }
                }
                else if (go is GeometryInstance gi)
                {
                    CollectPoints(gi.GetInstanceGeometry(), pts);
                }
            }
        }

        private static XYZ ApplyCov(List<XYZ> pts, XYZ c, XYZ v)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts)
            {
                var d = p - c;
                var k = d.DotProduct(v);
                x += k * d.X; y += k * d.Y; z += k * d.Z;
            }
            return new XYZ(x, y, z);
        }


        private static XYZ Scale(XYZ v, double k)
        {
            return new XYZ(v.X * k, v.Y * k, v.Z * k);
        }

        private static XYZ Add(XYZ a, XYZ b)
        {
            return new XYZ(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        private static XYZ Sub(XYZ a, XYZ b)
        {
            return new XYZ(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        private static XYZ Normalize(XYZ v)
        {
            var l = v.GetLength();
            if (l < 1e-9) return XYZ.BasisX;
            return new XYZ(v.X / l, v.Y / l, v.Z / l);
        }

        private static XYZ GetBBoxMin(Element el)
        {
            try { return el?.get_BoundingBox(null)?.Min; } catch { return null; }
        }

        private static XYZ GetBBoxMax(Element el)
        {
            try { return el?.get_BoundingBox(null)?.Max; } catch { return null; }
        }

        private static XYZ GetLocation(Element el)
        {
            if (el?.Location is LocationPoint lp) return lp.Point;
            if (el?.Location is LocationCurve lc) return lc.Curve?.Evaluate(0.5, true);

            try
            {
                var bb = el?.get_BoundingBox(null);
                if (bb != null)
                {
                    return new XYZ(
                        (bb.Min.X + bb.Max.X) / 2.0,
                        (bb.Min.Y + bb.Max.Y) / 2.0,
                        (bb.Min.Z + bb.Max.Z) / 2.0);
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool HasMoved(XYZ oldPoint, XYZ newPoint)
        {
            if (oldPoint == null || newPoint == null) return false;
            return oldPoint.DistanceTo(newPoint) >= MinMoveFeet;
        }

        private static async Task WorkerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Flush();
                CompressOldFiles();
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
        }

        private static void Flush()
        {
            if (Queue.IsEmpty) return;
            var batch = new List<ElementHistoryEvent>();
            while (batch.Count < 200 && Queue.TryDequeue(out var ev)) batch.Add(ev);
            if (batch.Count == 0) return;
            var path = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, $"element-history-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (FileSync)
            {
                Directory.CreateDirectory(CollaborativeModelTrackerStore.ActiveDirectory);
                using (var sw = new StreamWriter(path, true, Encoding.UTF8))
                {
                    foreach (var ev in batch)
                        sw.WriteLine(JsonConvert.SerializeObject(ev, Formatting.None));
                }
            }
        }

        private static void CompressOldFiles()
        {
            var dir = CollaborativeModelTrackerStore.ActiveDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "element-history-*.jsonl"))
            {
                try
                {
                    var dt = File.GetLastWriteTimeUtc(file);
                    if ((DateTime.UtcNow - dt).TotalDays < 7) continue;
                    var gz = file + ".gz";
                    if (File.Exists(gz)) continue;
                    using (var src = File.OpenRead(file))
                    using (var dst = File.Create(gz))
                    using (var gzip = new GZipStream(dst, CompressionLevel.Optimal))
                        src.CopyTo(gzip);
                    File.Delete(file);
                }
                catch { }
            }
        }
    }
}

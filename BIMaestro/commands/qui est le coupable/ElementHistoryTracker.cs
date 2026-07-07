using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        public string ThumbnailPath { get; set; }
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
            public int TypeId { get; set; }
            public string Name { get; set; }
            public XYZ Location { get; set; }
            public XYZ BBoxMin { get; set; }
            public XYZ BBoxMax { get; set; }
            public List<XYZ> ObbCorners { get; set; }
            public GhostMeshSnapshot GhostMesh { get; set; }
            public bool DetailCaptureAttempted { get; set; }
            public Dictionary<string, string> Parameters { get; set; }
            public DateTime LastLogged { get; set; }
        }

        private sealed class GhostMeshSnapshot
        {
            public List<double[]> Vertices { get; set; } = new List<double[]>();
            public List<int[]> Faces { get; set; } = new List<int[]>();
        }

        private sealed class GhostMeshBuilder
        {
            private sealed class TriangleCandidate
            {
                public XYZ A { get; set; }
                public XYZ B { get; set; }
                public XYZ C { get; set; }
                public double Area { get; set; }
                public int Order { get; set; }
            }

            private readonly List<TriangleCandidate> _triangles = new List<TriangleCandidate>();
            private int _nextOrder;
            private int _minAreaIndex = -1;
            private double _minArea = double.MaxValue;

            public int CandidateCount => _triangles.Count;

            public void AddTriangle(XYZ a, XYZ b, XYZ c)
            {
                if (!IsUsefulTriangle(a, b, c)) return;

                var area = GetTriangleArea(a, b, c);
                if (area <= 1e-8) return;

                var candidate = new TriangleCandidate
                {
                    A = a,
                    B = b,
                    C = c,
                    Area = area,
                    Order = _nextOrder++
                };

                if (_triangles.Count < MaxGhostCandidateFaces)
                {
                    _triangles.Add(candidate);
                    if (area < _minArea)
                    {
                        _minArea = area;
                        _minAreaIndex = _triangles.Count - 1;
                    }
                    return;
                }

                if (area <= _minArea) return;

                if (_minAreaIndex < 0 || _minAreaIndex >= _triangles.Count)
                    RecalculateSmallestCandidate();

                _triangles[_minAreaIndex] = candidate;
                RecalculateSmallestCandidate();
            }

            public GhostMeshSnapshot ToSnapshot(out bool decimated)
            {
                decimated = _triangles.Count > MaxGhostPreviewFaces;
                if (_triangles.Count == 0) return null;

                var selected = _triangles
                    .OrderByDescending(x => x.Area)
                    .Take(MaxGhostPreviewFaces)
                    .OrderBy(x => x.Order)
                    .ToList();

                var vertexIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                var vertices = new List<double[]>();
                var faces = new List<int[]>();

                foreach (var triangle in selected)
                {
                    var ia = GetVertexIndex(triangle.A, vertexIndex, vertices);
                    var ib = GetVertexIndex(triangle.B, vertexIndex, vertices);
                    var ic = GetVertexIndex(triangle.C, vertexIndex, vertices);
                    if (ia < 0 || ib < 0 || ic < 0) continue;
                    if (ia == ib || ib == ic || ia == ic) continue;

                    faces.Add(new[] { ia, ib, ic });
                }

                if (faces.Count == 0 || vertices.Count == 0) return null;

                return new GhostMeshSnapshot
                {
                    Vertices = vertices,
                    Faces = faces
                };
            }

            private int GetVertexIndex(XYZ point, Dictionary<string, int> vertexIndex, List<double[]> vertices)
            {
                if (point == null) return -1;

                var key = BuildGhostVertexKey(point);
                if (vertexIndex.TryGetValue(key, out var existing))
                    return existing;

                if (vertices.Count >= MaxGhostPreviewVertices)
                    return -1;

                var index = vertices.Count;
                vertexIndex[key] = index;
                vertices.Add(new[]
                {
                    RoundGhostCoord(point.X),
                    RoundGhostCoord(point.Y),
                    RoundGhostCoord(point.Z)
                });
                return index;
            }

            private void RecalculateSmallestCandidate()
            {
                _minAreaIndex = -1;
                _minArea = double.MaxValue;

                for (int i = 0; i < _triangles.Count; i++)
                {
                    if (_triangles[i].Area >= _minArea) continue;
                    _minArea = _triangles[i].Area;
                    _minAreaIndex = i;
                }
            }
        }

        private sealed class ParameterChangeInfo
        {
            public string Name { get; set; }
            public string OldValue { get; set; }
            public string NewValue { get; set; }
        }

        private sealed class ImageFileCandidate
        {
            public string Path { get; set; }
            public string NameKey { get; set; }
            public HashSet<string> Tokens { get; set; }
        }

        private sealed class DeferredPrimeState
        {
            public DateTime NotBeforeUtc { get; set; }
            public Queue<ElementId> PendingElementIds { get; set; } = new Queue<ElementId>();
            public bool ElementIdsLoaded { get; set; }
        }

        private sealed class HistoryModelScope
        {
            public HashSet<string> Keys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Names { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly ConcurrentQueue<ElementHistoryEvent> Queue = new ConcurrentQueue<ElementHistoryEvent>();
        private static readonly ConcurrentDictionary<string, string> ThumbnailPathCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, List<ImageFileCandidate>> ImageIndexCache =
            new ConcurrentDictionary<string, List<ImageFileCandidate>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object StartSync = new object();
        private static readonly object FileSync = new object();
        private static readonly Dictionary<string, ElementSnapshot> SnapshotByElementId =
            new Dictionary<string, ElementSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ElementSnapshot> FamilyTypeSnapshotByKey =
            new Dictionary<string, ElementSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PrimedDocumentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> FamilyParameterPrimedDocumentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DeferredPrimeState> DeferredPrimeByDocumentKey =
            new Dictionary<string, DeferredPrimeState>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<int, string> RuntimeDocumentKeys =
            new ConcurrentDictionary<int, string>();
        private static readonly string RuntimeSessionId = Guid.NewGuid().ToString("N");
        private static CancellationTokenSource _cts;
        private static Task _worker;
        private const double MinMoveFeet = 0.00656168; // ~2 mm
        private const int DefaultElementHistoryTake = 2000;
        private const int DefaultModelHistoryTake = 2000;
        private const int DefaultDeletedHistoryTake = 2000;
        private const int MaxIndexedImageFiles = 12000;
        private const int MaxGhostPreviewFaces = 2400;
        private const int MaxGhostPreviewVertices = 3600;
        private const int MaxGhostCandidateFaces = 12000;
        private const int GhostCoordinateDecimals = 5;
        private const int DeferredPrimeBatchSize = 25;
        private const int DeferredPrimeTimeBudgetMs = 35;
        private const int MaxSelectionSnapshotCount = 25;
        private const int SelectionSnapshotTimeBudgetMs = 80;
        private const int MaxChangedElementSnapshotsPerTransaction = 250;
        private const int MaxDeletedElementSnapshotsPerTransaction = 500;
        private const int DocumentChangedTimeBudgetMs = 300;
        private const int SelectionDetailedGeometryTimeoutMs = 500;
        private static readonly TimeSpan DeferredPrimeDelay = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan HistoryMaintenanceInitialDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan HistoryMaintenanceInterval = TimeSpan.FromHours(1);
        private const bool IncludeAnnotationCategoriesForFuture = false;
        private const bool DefaultCaptureDetailedDeletedMesh = false;
        private static volatile bool _captureDetailedDeletedMesh = DefaultCaptureDetailedDeletedMesh;
        private static readonly string DeletedMeshModePreferencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RevitLogs",
            "SauvegardePréférence",
            "ElementHistoryDeletedMeshMode.json");
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
        private static readonly string[] IgnoredTransactionFragments =
        {
            "BIMaestro - Visualisation historique",
            "BIMaestro - Visualisation suppression",
            "BIMaestro - Nettoyer previews historique",
            "BIMaestro - Restaurer paramètres historique"
        };


        public static void Start()
        {
            LoadDeletedMeshModePreference();
            lock (StartSync)
            {
                if (_worker != null) return;
                _cts = new CancellationTokenSource();
                _worker = Task.Run(() => WorkerLoop(_cts.Token));
            }
        }

        internal static bool CaptureDetailedDeletedMesh
        {
            get { return _captureDetailedDeletedMesh; }
            set
            {
                _captureDetailedDeletedMesh = value;
                SaveDeletedMeshModePreference(value);
            }
        }

        private static void LoadDeletedMeshModePreference()
        {
            try
            {
                if (!File.Exists(DeletedMeshModePreferencePath))
                {
                    _captureDetailedDeletedMesh = DefaultCaptureDetailedDeletedMesh;
                    return;
                }

                var json = File.ReadAllText(DeletedMeshModePreferencePath);
                var pref = JsonConvert.DeserializeObject<DeletedMeshModePreference>(json);
                _captureDetailedDeletedMesh = pref?.Detailed ?? DefaultCaptureDetailedDeletedMesh;
            }
            catch
            {
                _captureDetailedDeletedMesh = DefaultCaptureDetailedDeletedMesh;
            }
        }

        private static void SaveDeletedMeshModePreference(bool detailed)
        {
            try
            {
                var dir = Path.GetDirectoryName(DeletedMeshModePreferencePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(new DeletedMeshModePreference { Detailed = detailed }, Formatting.Indented);
                File.WriteAllText(DeletedMeshModePreferencePath, json, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private sealed class DeletedMeshModePreference
        {
            public bool Detailed { get; set; }
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

        internal static void FlushPendingForHistory()
        {
            try { Flush(); }
            catch { }
        }

        public static void ScheduleDeferredPrime(Document doc)
        {
            if (doc == null) return;
            if (!ShouldScheduleDeferredPrime(doc)) return;

            var key = GetDocumentKey(doc);
            if (string.IsNullOrWhiteSpace(key)) return;

            lock (SnapshotByElementId)
            {
                if (PrimedDocumentKeys.Contains(key)) return;
                if (DeferredPrimeByDocumentKey.ContainsKey(key)) return;

                DeferredPrimeByDocumentKey[key] = new DeferredPrimeState
                {
                    NotBeforeUtc = DateTime.UtcNow.Add(DeferredPrimeDelay)
                };
            }
        }

        private static bool ShouldScheduleDeferredPrime(Document doc)
        {
            if (doc == null) return false;
            if (CaptureDetailedDeletedMesh) return true;

            try
            {
                return doc.IsFamilyDocument;
            }
            catch
            {
                return false;
            }
        }

        public static void ProcessDeferredPrime(Document doc)
        {
            if (doc == null) return;

            var key = GetDocumentKey(doc);
            if (string.IsNullOrWhiteSpace(key)) return;

            DeferredPrimeState state;
            lock (SnapshotByElementId)
            {
                if (PrimedDocumentKeys.Contains(key))
                {
                    DeferredPrimeByDocumentKey.Remove(key);
                    return;
                }

                if (!DeferredPrimeByDocumentKey.TryGetValue(key, out state))
                    return;
            }

            if (DateTime.UtcNow < state.NotBeforeUtc)
                return;

            try
            {
                if (!state.ElementIdsLoaded)
                    LoadDeferredPrimeElementIds(doc, state);

                int processed = 0;
                var deadlineUtc = DateTime.UtcNow.AddMilliseconds(DeferredPrimeTimeBudgetMs);
                while (processed < DeferredPrimeBatchSize
                       && state.PendingElementIds.Count > 0
                       && DateTime.UtcNow < deadlineUtc)
                {
                    var id = state.PendingElementIds.Dequeue();
                    var element = doc.GetElement(id);
                    PrimeElementSnapshot(element);
                    processed++;
                }

                if (state.PendingElementIds.Count > 0)
                    return;

                if (DateTime.UtcNow >= deadlineUtc)
                    return;

                PrimeFamilyDocumentTypes(doc);
                lock (SnapshotByElementId)
                {
                    PrimedDocumentKeys.Add(key);
                    FamilyParameterPrimedDocumentKeys.Add(key);
                    DeferredPrimeByDocumentKey.Remove(key);
                }
            }
            catch
            {
                lock (SnapshotByElementId)
                {
                    DeferredPrimeByDocumentKey.Remove(key);
                }
            }
        }

        private static void LoadDeferredPrimeElementIds(Document doc, DeferredPrimeState state)
        {
            if (doc == null || state == null || state.ElementIdsLoaded) return;

            foreach (var id in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds())
                state.PendingElementIds.Enqueue(id);

            foreach (var id in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).ToElementIds())
                state.PendingElementIds.Enqueue(id);

            state.ElementIdsLoaded = true;
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
                    PrimeElementSnapshot(el);
                }

                foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                {
                    PrimeElementSnapshot(symbol);
                }

                PrimeFamilyDocumentTypes(doc);
            }
            catch
            {
            }

            lock (SnapshotByElementId)
            {
                FamilyParameterPrimedDocumentKeys.Add(key);
            }
        }

        public static void PrimeFamilyParameterSnapshots(Document doc)
        {
            if (doc == null) return;

            var key = GetDocumentKey(doc);
            if (string.IsNullOrWhiteSpace(key)) return;

            lock (SnapshotByElementId)
            {
                if (FamilyParameterPrimedDocumentKeys.Contains(key)) return;
                FamilyParameterPrimedDocumentKeys.Add(key);
            }

            try
            {
                if (doc.IsFamilyDocument)
                {
                    PrimeFamilyDocumentTypes(doc);
                    return;
                }

                foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                    PrimeElementSnapshot(symbol);
            }
            catch
            {
            }
        }

        private static void PrimeElementSnapshot(Element el)
        {
            try
            {
                if (el == null || ShouldIgnoreElement(el)) return;
                var snapshot = BuildSnapshot(el, includeOrientedCorners: false);
                StoreSnapshot(el.Document, el.Id, snapshot);
            }
            catch
            {
            }
        }

        public static void CaptureSelectedElementDetails(Document doc, ICollection<ElementId> selectedIds)
        {
            if (doc == null || selectedIds == null || selectedIds.Count == 0) return;

            try
            {
                var deadlineUtc = DateTime.UtcNow.AddMilliseconds(SelectionSnapshotTimeBudgetMs);
                var processed = 0;

                foreach (var id in selectedIds)
                {
                    if (processed >= MaxSelectionSnapshotCount || DateTime.UtcNow >= deadlineUtc)
                        break;

                    if (id == null || id == ElementId.InvalidElementId) continue;

                    var element = doc.GetElement(id);
                    if (element == null) continue;
                    var keepSimpleOnly = ShouldKeepSelectionSnapshotSimple(element);
                    if (ShouldIgnoreElement(element) && !keepSimpleOnly) continue;

                    if (CaptureDetailedDeletedMesh && !keepSimpleOnly && HasDetailCaptureAttempted(doc, id)) continue;

                    var quickSnapshot = BuildSnapshot(element, includeOrientedCorners: false);
                    StoreSnapshot(doc, id, quickSnapshot);
                    processed++;

                    if (!CaptureDetailedDeletedMesh || keepSimpleOnly)
                    {
                        quickSnapshot.DetailCaptureAttempted = keepSimpleOnly;
                        StoreSnapshot(doc, id, quickSnapshot);
                        continue;
                    }

                    if (HasDetailCaptureAttempted(doc, id)) continue;

                    var snapshot = BuildSnapshot(
                        element,
                        includeOrientedCorners: true,
                        detailedGeometryTimeoutMs: SelectionDetailedGeometryTimeoutMs);
                    StoreSnapshot(doc, id, snapshot);
                }
            }
            catch
            {
            }
        }

        private static bool HasDetailCaptureAttempted(Document doc, ElementId id)
        {
            var key = BuildElementSnapshotKey(doc, id);
            if (string.IsNullOrWhiteSpace(key)) return false;

            lock (SnapshotByElementId)
            {
                return SnapshotByElementId.TryGetValue(key, out var snapshot)
                    && snapshot != null
                    && snapshot.DetailCaptureAttempted;
            }
        }

        private static bool IsDocumentPrimed(Document doc)
        {
            if (doc == null) return false;
            var key = GetDocumentKey(doc);
            lock (SnapshotByElementId)
            {
                return PrimedDocumentKeys.Contains(key);
            }
        }

        public static void CaptureDocumentChanges(Document doc, DocumentChangedEventArgs e)
        {
            if (doc == null || e == null) return;
            var user = Environment.UserName;
            var tx = e.GetTransactionNames()?.FirstOrDefault() ?? "Transaction";
            if (IsIgnoredTransaction(tx)) return;
            var addedIds = e.GetAddedElementIds().ToList();
            var modifiedIds = e.GetModifiedElementIds().ToList();
            var deletedIds = e.GetDeletedElementIds().ToList();
            var suppressSecondaryModifications = deletedIds.Count > 0;
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(DocumentChangedTimeBudgetMs);
            var relatedTypeParameterDeltaCache = new Dictionary<int, Dictionary<string, object>>();
            var processedChanges = 0;
            var processedDeletes = 0;

            foreach (var id in addedIds)
            {
                if (!CanContinueDocumentChangedCapture(deadlineUtc, processedChanges, MaxChangedElementSnapshotsPerTransaction))
                    break;

                if (CaptureAddedOrModified(doc, id, user, tx, isCreate: true, relatedTypeParameterDeltaCache: relatedTypeParameterDeltaCache))
                    processedChanges++;
            }

            foreach (var id in modifiedIds.OrderBy(id => IsFamilySymbolElementId(doc, id) ? 1 : 0))
            {
                if (!CanContinueDocumentChangedCapture(deadlineUtc, processedChanges, MaxChangedElementSnapshotsPerTransaction))
                    break;

                if (CaptureAddedOrModified(doc, id, user, tx, isCreate: false, suppressSecondaryModification: suppressSecondaryModifications, relatedTypeParameterDeltaCache: relatedTypeParameterDeltaCache))
                    processedChanges++;
            }

            foreach (var id in deletedIds)
            {
                if (!CanContinueDocumentChangedCapture(deadlineUtc, processedDeletes, MaxDeletedElementSnapshotsPerTransaction))
                    break;

                EnqueueDeleted(doc, id, user, tx);
                processedDeletes++;
            }

            if (DateTime.UtcNow < deadlineUtc)
                CaptureFamilyDocumentTypeChanges(doc, user, tx);
        }

        private static bool CanContinueDocumentChangedCapture(DateTime deadlineUtc, int processedCount, int maxCount)
        {
            return processedCount < maxCount && DateTime.UtcNow < deadlineUtc;
        }

        public static List<ElementHistoryEvent> LoadElementHistory(Document doc, Element element)
        {
            var uniqueId = element?.UniqueId;
            if (string.IsNullOrWhiteSpace(uniqueId)) return new List<ElementHistoryEvent>();
            return LoadMatchingHistory(GetDocumentKeysForHistory(doc), uniqueId, null, DefaultElementHistoryTake);
        }

        internal static List<ElementHistoryEvent> LoadElementHistory(string modelKey, string uniqueId, int take)
        {
            if (string.IsNullOrWhiteSpace(uniqueId)) return new List<ElementHistoryEvent>();
            return LoadMatchingHistory(new[] { modelKey }, uniqueId, null, take);
        }

        internal static List<ElementHistoryEvent> LoadElementHistory(IEnumerable<string> modelKeys, string uniqueId, int take)
        {
            if (string.IsNullOrWhiteSpace(uniqueId)) return new List<ElementHistoryEvent>();
            return LoadMatchingHistory(modelKeys, uniqueId, null, take);
        }


        public static List<ElementHistoryEvent> LoadRecentModelHistory(Document doc, int take = DefaultModelHistoryTake)
        {
            return LoadMatchingHistory(GetDocumentKeysForHistory(doc), null, null, take);
        }

        internal static List<ElementHistoryEvent> LoadRecentModelHistory(string modelKey, int take = DefaultModelHistoryTake)
        {
            return LoadMatchingHistory(new[] { modelKey }, null, null, take);
        }

        internal static List<ElementHistoryEvent> LoadRecentModelHistory(IEnumerable<string> modelKeys, int take = DefaultModelHistoryTake)
        {
            return LoadMatchingHistory(modelKeys, null, null, take);
        }

        internal static List<ElementHistoryEvent> LoadModelHistoryForLocalDate(string modelKey, DateTime localDate)
        {
            return LoadModelHistoryForLocalDate(new[] { modelKey }, localDate);
        }

        internal static List<ElementHistoryEvent> LoadModelHistoryForLocalDate(IEnumerable<string> modelKeys, DateTime localDate)
        {
            var scope = BuildHistoryModelScope(modelKeys);
            if (scope.Keys.Count == 0 && scope.Names.Count == 0)
                return new List<ElementHistoryEvent>();

            var day = localDate.Date;
            var startLocal = day;
            var endLocal = day.AddDays(1);
            var startUtc = startLocal.ToUniversalTime();
            var endUtc = endLocal.ToUniversalTime();

            var dir = CollaborativeModelTrackerStore.ActiveDirectory;
            var files = Directory.Exists(dir)
                ? GetHistoryFiles(dir).Where(f => MayContainUtcRange(f, startUtc, endUtc)).ToArray()
                : Array.Empty<string>();
            var result = new Dictionary<string, ElementHistoryEvent>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                foreach (var line in ReadHistoryLines(f))
                {
                    try
                    {
                        var ev = JsonConvert.DeserializeObject<ElementHistoryEvent>(line);
                        if (!MatchesHistoryFilter(ev, scope, null, null)) continue;

                        var localTs = ev.Ts.ToLocalTime();
                        if (localTs < startLocal || localTs >= endLocal) continue;

                        var key = BuildHistoryEventIdentity(ev);
                        if (!result.ContainsKey(key))
                            result[key] = ev;
                    }
                    catch { }
                }
            }

            return result.Values
                .OrderByDescending(x => x.Ts)
                .ToList();
        }

        public static List<ElementHistoryEvent> LoadRecentDeletedHistory(Document doc)
        {
            return LoadMatchingHistory(GetDocumentKeysForHistory(doc), null, "delete", DefaultDeletedHistoryTake);
        }

        internal static bool IsDisplayableHistoryEvent(ElementHistoryEvent ev)
        {
            if (ev == null) return false;
            if (IsLowValueParameterChange(ev)) return false;
            if (IsNoisy3DViewEvent(ev)) return false;
            return IsUsefulHistoryText(ev.Category) && !IsIgnoredCategoryName(ev.Category);
        }

        private static List<ElementHistoryEvent> LoadAllHistory(Document doc)
        {
            var files = Directory.Exists(CollaborativeModelTrackerStore.ActiveDirectory)
                ? GetHistoryFiles(CollaborativeModelTrackerStore.ActiveDirectory)
                : Array.Empty<string>();
            var result = new List<ElementHistoryEvent>();
            foreach (var f in files)
            {
                foreach (var line in ReadHistoryLines(f))
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

        private static List<ElementHistoryEvent> LoadMatchingHistory(string modelKey, string uniqueId, string action, int take)
        {
            return LoadMatchingHistory(new[] { modelKey }, uniqueId, action, take);
        }

        private static List<ElementHistoryEvent> LoadMatchingHistory(IEnumerable<string> modelKeys, string uniqueId, string action, int take)
        {
            var scope = BuildHistoryModelScope(modelKeys);
            if (scope.Keys.Count == 0 && scope.Names.Count == 0)
                return new List<ElementHistoryEvent>();

            var limit = Math.Max(1, take);
            var dir = CollaborativeModelTrackerStore.ActiveDirectory;
            var files = Directory.Exists(dir) ? GetHistoryFiles(dir) : Array.Empty<string>();
            var result = new List<ElementHistoryEvent>();

            foreach (var f in files)
            {
                var fileMatches = new List<ElementHistoryEvent>();
                foreach (var line in ReadHistoryLines(f))
                {
                    try
                    {
                        var ev = JsonConvert.DeserializeObject<ElementHistoryEvent>(line);
                        if (!MatchesHistoryFilter(ev, scope, uniqueId, action)) continue;
                        fileMatches.Add(ev);
                    }
                    catch { }
                }

                if (fileMatches.Count == 0) continue;
                result.AddRange(fileMatches.OrderByDescending(x => x.Ts));
                if (result.Count >= limit)
                    break;
            }

            return result
                .OrderByDescending(x => x.Ts)
                .Take(limit)
                .ToList();
        }

        private static bool MatchesHistoryFilter(ElementHistoryEvent ev, string modelKey, string uniqueId, string action)
        {
            return MatchesHistoryFilter(ev, BuildHistoryModelScope(new[] { modelKey }), uniqueId, action);
        }

        private static bool MatchesHistoryFilter(ElementHistoryEvent ev, HistoryModelScope scope, string uniqueId, string action)
        {
            if (ev == null) return false;
            if (scope != null && (scope.Keys.Count > 0 || scope.Names.Count > 0) && !MatchesHistoryModelScope(ev.ModelKey, scope))
                return false;
            if (!string.IsNullOrWhiteSpace(uniqueId)
                && !string.Equals(ev.UniqueId ?? string.Empty, uniqueId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(action)
                && !string.Equals(ev.Action ?? string.Empty, action, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private static HistoryModelScope BuildHistoryModelScope(IEnumerable<string> modelKeys)
        {
            var scope = new HistoryModelScope();
            foreach (var key in modelKeys ?? Enumerable.Empty<string>())
            {
                AddHistoryModelKey(scope, key);
            }
            return scope;
        }

        private static void AddHistoryModelKey(HistoryModelScope scope, string key)
        {
            if (scope == null || string.IsNullOrWhiteSpace(key)) return;
            var trimmed = key.Trim();
            if (trimmed.Length == 0) return;

            scope.Keys.Add(trimmed);
            foreach (var name in GetHistoryModelNames(trimmed))
                scope.Names.Add(name);
        }

        private static bool MatchesHistoryModelScope(string eventModelKey, HistoryModelScope scope)
        {
            if (scope == null) return true;
            if (string.IsNullOrWhiteSpace(eventModelKey)) return false;

            var key = eventModelKey.Trim();
            if (scope.Keys.Contains(key)) return true;

            foreach (var name in GetHistoryModelNames(key))
            {
                if (scope.Names.Contains(name))
                    return true;
            }

            return false;
        }

        private static IEnumerable<string> GetHistoryModelNames(string modelKey)
        {
            if (string.IsNullOrWhiteSpace(modelKey)) yield break;

            foreach (var value in GetHistoryModelNameCandidates(modelKey))
            {
                var normalized = NormalizeImageKey(value);
                if (IsUsefulHistoryModelName(normalized))
                    yield return normalized;
            }
        }

        private static IEnumerable<string> GetHistoryModelNameCandidates(string modelKey)
        {
            var value = (modelKey ?? string.Empty).Trim();
            if (value.Length == 0) yield break;

            yield return value;

            var unsavedParts = value.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (unsavedParts.Length > 0)
                yield return unsavedParts[unsavedParts.Length - 1];

            string fileName = null;
            try { fileName = Path.GetFileNameWithoutExtension(value); }
            catch { }

            if (!string.IsNullOrWhiteSpace(fileName))
                yield return fileName;
        }

        private static bool IsUsefulHistoryModelName(string normalizedName)
        {
            if (string.IsNullOrWhiteSpace(normalizedName)) return false;
            var value = normalizedName.Trim();
            return value.Length >= 3
                && !value.Equals("unsaved", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("projet sans nom", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("project", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildHistoryEventIdentity(ElementHistoryEvent ev)
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

        private static bool MayContainUtcRange(string path, DateTime startUtc, DateTime endUtc)
        {
            var token = GetHistoryDateToken(path);
            if (DateTime.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                var fileStart = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
                var fileEnd = fileStart.AddDays(1);
                return fileStart < endUtc && fileEnd > startUtc;
            }

            if (DateTime.TryParseExact(token, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            {
                var fileStart = DateTime.SpecifyKind(month.Date, DateTimeKind.Utc);
                var fileEnd = fileStart.AddMonths(1);
                return fileStart < endUtc && fileEnd > startUtc;
            }

            return true;
        }

        private static string[] GetHistoryFiles(string dir)
        {
            try
            {
                return Directory.GetFiles(dir, "element-history-*.jsonl")
                    .Concat(Directory.GetFiles(dir, "element-history-*.jsonl.gz"))
                    .OrderByDescending(GetHistoryFileSortDate)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> ReadHistoryLines(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                yield break;

            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using (var file = File.OpenRead(path))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        yield return line;
                }

                yield break;
            }

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
                yield return line;
        }

        private static bool CaptureAddedOrModified(
            Document doc,
            ElementId id,
            string user,
            string tx,
            bool isCreate,
            bool suppressSecondaryModification = false,
            Dictionary<int, Dictionary<string, object>> relatedTypeParameterDeltaCache = null)
        {
            if (id == null || id == ElementId.InvalidElementId) return false;
            var el = doc.GetElement(id);
            if (el == null || ShouldIgnoreElement(el)) return false;

            Dictionary<string, object> relatedTypeParameterDelta = null;
            if (!isCreate && !(el is FamilySymbol))
                relatedTypeParameterDelta = CaptureRelatedFamilyTypeParameterDelta(doc, el, relatedTypeParameterDeltaCache);

            var current = BuildSnapshot(el, includeOrientedCorners: CaptureDetailedDeletedMesh);
            ElementSnapshot previous;
            lock (SnapshotByElementId)
            {
                SnapshotByElementId.TryGetValue(BuildElementSnapshotKey(doc, id), out previous);
            }

            var action = isCreate ? "create" : DetermineAction(previous, current);
            var delta = BuildDelta(action, previous, current);
            if (!isCreate && HasParameterDelta(relatedTypeParameterDelta))
            {
                action = "param_change";
                delta = relatedTypeParameterDelta;
            }

            if (!isCreate && (suppressSecondaryModification || action == "modify_skip" || IsLowValueParameterChange(action, delta)))
            {
                StoreSnapshot(doc, id, current);
                return true;
            }

            if (!isCreate && IsNoisy3DViewChange(action, current, delta))
            {
                StoreSnapshot(doc, id, current);
                return true;
            }

            Queue.Enqueue(new ElementHistoryEvent
            {
                Ts = DateTime.UtcNow,
                Project = doc.ProjectInformation?.Name ?? "Projet",
                ModelGuid = doc.GetHashCode().ToString(CultureInfo.InvariantCulture),
                ModelKey = GetDocumentKey(doc),
                ElementId = id.GetIdValue(),
                UniqueId = current.UniqueId,
                Category = current.Category,
                Family = current.Family,
                TypeName = current.TypeName,
                ThumbnailPath = ResolveThumbnailPath(doc, current.TypeId, current.Family, current.TypeName),
                Action = action,
                User = user,
                Tx = tx,
                Delta = delta
            });

            StoreSnapshot(doc, id, current);
            return true;
        }

        private static bool IsFamilySymbolElementId(Document doc, ElementId id)
        {
            try
            {
                if (doc == null || id == null || id == ElementId.InvalidElementId) return false;
                return doc.GetElement(id) is FamilySymbol;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, object> CaptureRelatedFamilyTypeParameterDelta(
            Document doc,
            Element el,
            Dictionary<int, Dictionary<string, object>> relatedTypeParameterDeltaCache)
        {
            try
            {
                var typeId = el.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;
                var typeIdValue = typeId.GetIdValue();

                if (relatedTypeParameterDeltaCache != null
                    && relatedTypeParameterDeltaCache.TryGetValue(typeIdValue, out var cachedDelta))
                    return cachedDelta;

                var type = doc.GetElement(typeId) as FamilySymbol;
                if (type == null || ShouldIgnoreElement(type))
                {
                    relatedTypeParameterDeltaCache?.Add(typeIdValue, null);
                    return null;
                }

                var current = BuildSnapshot(type, includeOrientedCorners: false);
                ElementSnapshot previous;
                lock (SnapshotByElementId)
                {
                    SnapshotByElementId.TryGetValue(BuildElementSnapshotKey(doc, type.Id), out previous);
                }

                var action = DetermineAction(previous, current);
                var delta = BuildDelta(action, previous, current);
                StoreSnapshot(doc, type.Id, current);

                var result = string.Equals(action, "param_change", StringComparison.OrdinalIgnoreCase)
                             && HasParameterDelta(delta)
                    ? delta
                    : null;

                relatedTypeParameterDeltaCache?.Add(typeIdValue, result);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static void PrimeFamilyDocumentTypes(Document doc)
        {
            foreach (var item in BuildFamilyDocumentTypeSnapshots(doc))
                StoreFamilyTypeSnapshot(item.Key, item.Value);
        }

        private static void CaptureFamilyDocumentTypeChanges(Document doc, string user, string tx)
        {
            foreach (var item in BuildFamilyDocumentTypeSnapshots(doc))
            {
                ElementSnapshot previous;
                lock (SnapshotByElementId)
                {
                    FamilyTypeSnapshotByKey.TryGetValue(item.Key, out previous);
                }

                var current = item.Value;
                var action = DetermineAction(previous, current);
                var delta = BuildDelta(action, previous, current);
                if (previous == null || action == "modify_skip" || IsLowValueParameterChange(action, delta))
                {
                    StoreFamilyTypeSnapshot(item.Key, current);
                    continue;
                }

                Queue.Enqueue(new ElementHistoryEvent
                {
                    Ts = DateTime.UtcNow,
                    Project = doc.ProjectInformation?.Name ?? doc.Title ?? "Famille",
                    ModelGuid = doc.GetHashCode().ToString(CultureInfo.InvariantCulture),
                    ModelKey = GetDocumentKey(doc),
                    ElementId = current.TypeId,
                    UniqueId = current.UniqueId,
                    Category = current.Category,
                    Family = current.Family,
                    TypeName = current.TypeName,
                    ThumbnailPath = null,
                    Action = action,
                    User = user,
                    Tx = tx,
                    Delta = delta
                });

                StoreFamilyTypeSnapshot(item.Key, current);
            }
        }

        private static void CaptureProjectFamilySymbolChanges(Document doc, string user, string tx)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument) return;

                foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                {
                    try
                    {
                        if (symbol == null || ShouldIgnoreElement(symbol)) continue;

                        var current = BuildSnapshot(symbol, includeOrientedCorners: false);
                        ElementSnapshot previous;
                        lock (SnapshotByElementId)
                        {
                            SnapshotByElementId.TryGetValue(BuildElementSnapshotKey(doc, symbol.Id), out previous);
                        }

                        var action = DetermineAction(previous, current);
                        var delta = BuildDelta(action, previous, current);
                        if (previous == null || action == "modify_skip" || IsLowValueParameterChange(action, delta))
                        {
                            StoreSnapshot(doc, symbol.Id, current);
                            continue;
                        }

                        Queue.Enqueue(new ElementHistoryEvent
                        {
                            Ts = DateTime.UtcNow,
                            Project = doc.ProjectInformation?.Name ?? doc.Title ?? "Projet",
                            ModelGuid = doc.GetHashCode().ToString(CultureInfo.InvariantCulture),
                            ModelKey = GetDocumentKey(doc),
                            ElementId = symbol.Id.GetIdValue(),
                            UniqueId = current.UniqueId,
                            Category = current.Category,
                            Family = current.Family,
                            TypeName = current.TypeName,
                            ThumbnailPath = ResolveThumbnailPath(doc, current.TypeId, current.Family, current.TypeName),
                            Action = action,
                            User = user,
                            Tx = tx,
                            Delta = delta
                        });

                        StoreSnapshot(doc, symbol.Id, current);
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

        private static void StoreFamilyTypeSnapshot(string key, ElementSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(key) || snapshot == null) return;
            snapshot.LastLogged = DateTime.UtcNow;
            lock (SnapshotByElementId)
            {
                FamilyTypeSnapshotByKey[key] = snapshot;
            }
        }

        private static void StoreSnapshot(Document doc, ElementId id, ElementSnapshot snapshot)
        {
            if (id == null || id == ElementId.InvalidElementId || snapshot == null) return;
            snapshot.LastLogged = DateTime.UtcNow;
            var key = BuildElementSnapshotKey(doc, id);
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (SnapshotByElementId)
            {
                SnapshotByElementId[key] = snapshot;
            }
        }

        private static string BuildElementSnapshotKey(Document doc, ElementId id)
        {
            if (doc == null || id == null || id == ElementId.InvalidElementId) return string.Empty;
            var modelKey = GetDocumentKey(doc);
            if (string.IsNullOrWhiteSpace(modelKey)) return string.Empty;
            return modelKey + "|element|" + id.GetIdValue().ToString(CultureInfo.InvariantCulture);
        }

        private static ElementSnapshot BuildSnapshot(Element el, bool includeOrientedCorners, int detailedGeometryTimeoutMs = 0)
        {
            string family = null;
            string typeName = null;
            var typeId = ElementId.InvalidElementId;
            var categoryName = CleanHistoryText(el.Category?.Name);

            if (el is FamilySymbol symbol)
            {
                family = symbol.FamilyName;
                typeName = symbol.Name;
                typeId = symbol.Id;
                if (!IsUsefulHistoryText(categoryName))
                    categoryName = "Type de famille";
            }
            else
            {
                try
                {
                    typeId = el.GetTypeId();
                    var type = el.Document.GetElement(typeId) as ElementType;
                    typeName = type?.Name;
                    if (type is FamilySymbol fs) family = fs.FamilyName;
                }
                catch { }
            }

            var parameters = CaptureWritableParameters(el);
            AddTypeParametersToSnapshot(el, parameters);

            var snapshot = new ElementSnapshot
            {
                UniqueId = el.UniqueId,
                Category = categoryName,
                Family = CleanHistoryText(family),
                TypeName = CleanHistoryText(typeName),
                TypeId = typeId == null || typeId == ElementId.InvalidElementId ? -1 : typeId.GetIdValue(),
                Name = el.Name ?? string.Empty,
                Location = GetLocation(el),
                BBoxMin = GetBBoxMin(el),
                BBoxMax = GetBBoxMax(el),
                DetailCaptureAttempted = includeOrientedCorners,
                Parameters = parameters
            };

            if (includeOrientedCorners && !ShouldKeepDetailedSnapshotSimple(el))
            {
                var deadlineUtc = detailedGeometryTimeoutMs > 0
                    ? DateTime.UtcNow.AddMilliseconds(detailedGeometryTimeoutMs)
                    : DateTime.MaxValue;

                snapshot.GhostMesh = CaptureGhostMesh(el, deadlineUtc);
                if (!IsDeadlineExpired(deadlineUtc))
                    snapshot.ObbCorners = GetOrientedCorners(el, deadlineUtc);
            }

            return snapshot;
        }

        private static string DetermineAction(ElementSnapshot previous, ElementSnapshot current)
        {
            if (previous == null) return "modify";
            if (!string.Equals(previous.TypeName, current.TypeName, StringComparison.OrdinalIgnoreCase)) return "type_change";
            if (HasMoved(previous.Location, current.Location)) return "move";
            if (GetParameterChanges(previous, current, 1).Count > 0) return "param_change";
            if (HasShapeChanged(previous, current)) return "geometry_change";

            var now = DateTime.UtcNow;
            if ((now - previous.LastLogged).TotalSeconds < 3) return "modify_skip";
            return "modify_skip";
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

            if (action == "param_change")
            {
                var changes = GetParameterChanges(previous, current, 12);
                if (changes.Count == 0) return null;
                var delta = new Dictionary<string, object>
                {
                    ["parameters"] = changes.ToArray()
                };

                if (previous?.Location != null && current?.Location != null && HasMoved(previous.Location, current.Location))
                {
                    delta["old"] = new { x = previous.Location.X, y = previous.Location.Y, z = previous.Location.Z };
                    delta["new"] = new { x = current.Location.X, y = current.Location.Y, z = current.Location.Z };
                    delta["dx"] = current.Location.X - previous.Location.X;
                    delta["dy"] = current.Location.Y - previous.Location.Y;
                    delta["dz"] = current.Location.Z - previous.Location.Z;
                }

                return delta;
            }

            if (action == "geometry_change")
            {
                var delta = new Dictionary<string, object>
                {
                    ["oldBox"] = BuildBoxDelta(previous),
                    ["newBox"] = BuildBoxDelta(current)
                };

                var oldSize = BuildBoxSizeDelta(previous);
                var newSize = BuildBoxSizeDelta(current);
                if (oldSize != null && newSize != null)
                {
                    delta["oldSize"] = oldSize;
                    delta["newSize"] = newSize;
                }

                return delta;
            }
            return null;
        }

        private static object BuildBoxDelta(ElementSnapshot snapshot)
        {
            if (snapshot == null || snapshot.BBoxMin == null || snapshot.BBoxMax == null) return null;
            return new
            {
                min = new { x = snapshot.BBoxMin.X, y = snapshot.BBoxMin.Y, z = snapshot.BBoxMin.Z },
                max = new { x = snapshot.BBoxMax.X, y = snapshot.BBoxMax.Y, z = snapshot.BBoxMax.Z }
            };
        }

        private static object BuildBoxSizeDelta(ElementSnapshot snapshot)
        {
            if (snapshot == null || snapshot.BBoxMin == null || snapshot.BBoxMax == null) return null;
            return new
            {
                x = snapshot.BBoxMax.X - snapshot.BBoxMin.X,
                y = snapshot.BBoxMax.Y - snapshot.BBoxMin.Y,
                z = snapshot.BBoxMax.Z - snapshot.BBoxMin.Z
            };
        }

        private static Dictionary<string, string> CaptureWritableParameters(Element el)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (el == null) return result;
            var captureReadOnlyParameters = el is FamilySymbol;

            try
            {
                foreach (var parameter in GetElementParametersForHistory(el))
                {
                    try
                    {
                        if (parameter == null) continue;
                        if (parameter.IsReadOnly && !captureReadOnlyParameters) continue;
                        var name = CleanHistoryText(parameter.Definition?.Name);
                        if (!IsUsefulHistoryText(name) || IsIgnoredParameterName(name)) continue;

                        var value = parameter.HasValue ? ReadParameterValue(parameter) : string.Empty;
                        if (value == null) value = string.Empty;
                        if (!result.ContainsKey(name))
                            result[name] = value;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static void AddTypeParametersToSnapshot(Element el, Dictionary<string, string> parameters)
        {
            if (el == null || parameters == null || el is FamilySymbol) return;

            try
            {
                var typeId = el.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return;

                var type = el.Document?.GetElement(typeId);
                if (type == null || type.Id == el.Id || ShouldIgnoreElement(type)) return;

                foreach (var pair in CaptureWritableParameters(type))
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    if (!parameters.ContainsKey(pair.Key))
                        parameters[pair.Key] = pair.Value ?? string.Empty;
                }
            }
            catch
            {
            }
        }

        private static List<Parameter> GetElementParametersForHistory(Element el)
        {
            var result = new List<Parameter>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (el == null) return result;

            try
            {
                var ordered = el.GetOrderedParameters();
                if (ordered != null)
                {
                    foreach (var parameter in ordered)
                        AddParameterForHistory(result, seen, parameter);
                }
            }
            catch
            {
            }

            try
            {
                foreach (Parameter parameter in el.Parameters)
                    AddParameterForHistory(result, seen, parameter);
            }
            catch
            {
            }

            try
            {
                var map = el.ParametersMap;
                var iterator = map?.ForwardIterator();
                if (iterator != null)
                {
                    iterator.Reset();
                    while (iterator.MoveNext())
                        AddParameterForHistory(result, seen, iterator.Current as Parameter);
                }
            }
            catch
            {
            }

            return result;
        }

        private static void AddParameterForHistory(List<Parameter> result, HashSet<string> seen, Parameter parameter)
        {
            if (result == null || seen == null || parameter == null) return;
            var key = GetParameterKeyForHistory(parameter);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) return;
            result.Add(parameter);
        }

        private static string GetParameterKeyForHistory(Parameter parameter)
        {
            if (parameter == null) return string.Empty;
            var name = CleanHistoryText(parameter.Definition?.Name);
            if (!IsUsefulHistoryText(name)) return string.Empty;
            return name + "|" + parameter.StorageType;
        }

        private static Dictionary<string, ElementSnapshot> BuildFamilyDocumentTypeSnapshots(Document doc)
        {
            var result = new Dictionary<string, ElementSnapshot>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return result;

            try
            {
                if (!doc.IsFamilyDocument) return result;
            }
            catch
            {
                return result;
            }

            FamilyManager manager;
            try
            {
                manager = doc.FamilyManager;
            }
            catch
            {
                return result;
            }

            var familyName = CleanHistoryText(GetFamilyDocumentName(doc));
            var typeId = GetFamilyDocumentElementId(doc);

            foreach (FamilyType familyType in manager.Types)
            {
                try
                {
                    if (familyType == null) continue;
                    var typeName = CleanHistoryText(familyType.Name);
                    if (!IsUsefulHistoryText(typeName)) continue;

                    var snapshot = new ElementSnapshot
                    {
                        UniqueId = BuildFamilyTypeUniqueId(doc, familyName, typeName),
                        Category = "Famille",
                        Family = familyName,
                        TypeName = typeName,
                        TypeId = typeId,
                        Name = string.Join(" : ", new[] { familyName, typeName }.Where(IsUsefulHistoryText)),
                        Parameters = CaptureFamilyTypeParameters(doc, manager, familyType)
                    };

                    result[BuildFamilyTypeSnapshotKey(doc, typeName)] = snapshot;
                }
                catch
                {
                }
            }

            return result;
        }

        private static Dictionary<string, string> CaptureFamilyTypeParameters(Document doc, FamilyManager manager, FamilyType familyType)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (manager == null || familyType == null) return result;

            foreach (FamilyParameter parameter in manager.Parameters)
            {
                try
                {
                    if (parameter == null) continue;
                    var name = CleanHistoryText(parameter.Definition?.Name);
                    if (!IsUsefulHistoryText(name) || IsIgnoredParameterName(name)) continue;

                    var value = ReadFamilyTypeParameterValue(doc, familyType, parameter);
                    if (value == null) continue;
                    result[name] = value;

                    var formula = CleanHistoryText(parameter.Formula);
                    if (IsUsefulHistoryText(formula))
                        result[name + " (formule)"] = formula;
                }
                catch
                {
                }
            }

            return result;
        }

        private static string ReadFamilyTypeParameterValue(Document doc, FamilyType familyType, FamilyParameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return familyType.AsString(parameter)?.Trim();
                    case StorageType.Integer:
                        return familyType.AsInteger(parameter)?.ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        var doubleValue = familyType.AsDouble(parameter);
                        return doubleValue == null
                            ? null
                            : FormatFamilyDoubleValue(doc, parameter, doubleValue.Value);
                    case StorageType.ElementId:
                        var id = familyType.AsElementId(parameter);
                        if (id == null) return string.Empty;
                        var elementName = doc?.GetElement(id)?.Name;
                        return IsUsefulHistoryText(elementName)
                            ? elementName
                            : id.GetIdValue().ToString(CultureInfo.InvariantCulture);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FormatFamilyDoubleValue(Document doc, FamilyParameter parameter, double value)
        {
            try
            {
                var units = doc?.GetUnits();
                var specTypeId = parameter?.Definition?.GetDataType();
                if (units != null && specTypeId != null && UnitUtils.IsMeasurableSpec(specTypeId))
                {
                    var formatted = UnitFormatUtils.Format(units, specTypeId, value, false);
                    if (!string.IsNullOrWhiteSpace(formatted))
                        return formatted.Trim();
                }
            }
            catch
            {
            }

            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static string BuildFamilyTypeSnapshotKey(Document doc, string typeName)
        {
            return GetDocumentKey(doc) + "|family-type|" + (typeName ?? string.Empty).Trim();
        }

        private static string BuildFamilyTypeUniqueId(Document doc, string familyName, string typeName)
        {
            return GetDocumentKey(doc) + "|family-type|" + (familyName ?? string.Empty).Trim() + "|" + (typeName ?? string.Empty).Trim();
        }

        private static string GetFamilyDocumentName(Document doc)
        {
            try
            {
                var ownerName = doc.OwnerFamily?.Name;
                if (IsUsefulHistoryText(ownerName)) return ownerName;
            }
            catch
            {
            }

            return doc?.Title ?? "Famille";
        }

        private static int GetFamilyDocumentElementId(Document doc)
        {
            try
            {
                var id = doc.OwnerFamily?.Id;
                if (id != null && id != ElementId.InvalidElementId)
                    return id.GetIdValue();
            }
            catch
            {
            }

            return -1;
        }

        private static string ReadParameterValue(Parameter parameter)
        {
            try
            {
                var display = parameter.AsValueString();
                if (!string.IsNullOrWhiteSpace(display)) return display.Trim();
            }
            catch
            {
            }

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString()?.Trim();
                    case StorageType.Integer:
                        return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        return parameter.AsDouble().ToString("G17", CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        var id = parameter.AsElementId();
                        return id == null ? string.Empty : id.GetIdValue().ToString(CultureInfo.InvariantCulture);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool IsIgnoredParameterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            return name.Equals("ElementId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("UniqueId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Family", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Category", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ParameterChangeInfo> GetParameterChanges(ElementSnapshot previous, ElementSnapshot current, int maxCount)
        {
            var result = new List<ParameterChangeInfo>();
            if (previous?.Parameters == null || current?.Parameters == null) return result;

            foreach (var pair in current.Parameters.OrderBy(x => x.Key))
            {
                previous.Parameters.TryGetValue(pair.Key, out var oldValue);
                var newValue = pair.Value ?? string.Empty;
                if (AreSameHistoryValue(oldValue, newValue)) continue;

                result.Add(new ParameterChangeInfo
                {
                    Name = pair.Key,
                    OldValue = oldValue ?? string.Empty,
                    NewValue = newValue
                });
                if (result.Count >= maxCount) return result;
            }

            foreach (var pair in previous.Parameters.OrderBy(x => x.Key))
            {
                if (current.Parameters.ContainsKey(pair.Key)) continue;
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;

                result.Add(new ParameterChangeInfo
                {
                    Name = pair.Key,
                    OldValue = pair.Value,
                    NewValue = string.Empty
                });
                if (result.Count >= maxCount) return result;
            }

            return result;
        }

        private static bool AreSameHistoryValue(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasShapeChanged(ElementSnapshot previous, ElementSnapshot current)
        {
            if (previous?.BBoxMin == null || previous.BBoxMax == null || current?.BBoxMin == null || current.BBoxMax == null)
                return false;

            return previous.BBoxMin.DistanceTo(current.BBoxMin) > MinMoveFeet
                || previous.BBoxMax.DistanceTo(current.BBoxMax) > MinMoveFeet;
        }

        private static bool IsLowValueParameterChange(ElementHistoryEvent ev)
        {
            if (ev == null || !string.Equals(ev.Action, "param_change", StringComparison.OrdinalIgnoreCase)) return false;
            return !HasParameterDelta(ev.Delta);
        }

        private static bool IsLowValueParameterChange(string action, Dictionary<string, object> delta)
        {
            if (!string.Equals(action, "param_change", StringComparison.OrdinalIgnoreCase)) return false;
            return !HasParameterDelta(delta);
        }

        private static bool HasParameterDelta(Dictionary<string, object> delta)
        {
            if (delta == null || !delta.TryGetValue("parameters", out var value) || value == null) return false;
            if (value is JArray jArray) return jArray.Count > 0;
            if (value is string) return false;

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var _ in enumerable)
                    return true;
            }

            return false;
        }

        private static bool IsNoisy3DViewEvent(ElementHistoryEvent ev)
        {
            if (ev == null) return false;
            if (IsCameraCategoryName(ev.Category)) return true;
            if (!Is3DViewText(ev.Category) && !Is3DViewText(ev.Family) && !Is3DViewText(ev.TypeName)) return false;

            if (string.Equals(ev.Action, "move", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(ev.Action, "param_change", StringComparison.OrdinalIgnoreCase)
                   && IsNoisy3DViewParameterDelta(ev.Delta);
        }

        private static bool IsNoisy3DViewChange(string action, ElementSnapshot current, Dictionary<string, object> delta)
        {
            if (current == null) return false;
            if (IsCameraCategoryName(current.Category)) return true;
            if (!Is3DViewText(current.Category) && !Is3DViewText(current.Family) && !Is3DViewText(current.TypeName) && !Is3DViewText(current.Name)) return false;

            if (string.Equals(action, "move", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(action, "param_change", StringComparison.OrdinalIgnoreCase)
                   && IsNoisy3DViewParameterDelta(delta);
        }

        private static bool IsNoisy3DViewParameterDelta(Dictionary<string, object> delta)
        {
            var names = ReadDeltaParameterNames(delta).ToList();
            if (names.Count == 0) return false;

            var hasCameraParameter = names.Any(Is3DViewCameraParameterName);
            return hasCameraParameter
                   && names.All(name => Is3DViewCameraParameterName(name) || Is3DViewSectionBoxParameterName(name));
        }

        private static IEnumerable<string> ReadDeltaParameterNames(Dictionary<string, object> delta)
        {
            if (delta == null || !delta.TryGetValue("parameters", out var value) || value == null) yield break;

            if (value is JArray jArray)
            {
                foreach (var token in jArray)
                {
                    var name = token?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) yield return name;
                }
                yield break;
            }

            if (value is string) yield break;

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    if (item is JObject jObject)
                    {
                        var name = jObject["name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) yield return name;
                        continue;
                    }

                    var property = item.GetType().GetProperty("name") ?? item.GetType().GetProperty("Name");
                    var propertyValue = property?.GetValue(item, null)?.ToString();
                    if (!string.IsNullOrWhiteSpace(propertyValue)) yield return propertyValue;
                }
            }
        }

        private static bool IsCameraCategoryName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("caméra", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("caméras", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Is3DViewText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim();
            return value.IndexOf("vue 3d", StringComparison.OrdinalIgnoreCase) >= 0
                   || value.IndexOf("3d view", StringComparison.OrdinalIgnoreCase) >= 0
                   || value.IndexOf("{3d", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Is3DViewCameraParameterName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("élévation cible", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("elevation cible", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("target elevation", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("élévation de l'oeil", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("élévation de l'œil", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("elevation de l'oeil", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("eye elevation", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("caméra", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("orientation", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Is3DViewSectionBoxParameterName(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.IndexOf("zone de coupe", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("section box", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("boîte de coupe", StringComparison.OrdinalIgnoreCase) >= 0
                   || text.IndexOf("boite de coupe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void EnqueueDeleted(Document doc, ElementId id, string user, string tx)
        {
            if (id == null || id == ElementId.InvalidElementId) return;

            ElementSnapshot snapshot = null;
            var key = BuildElementSnapshotKey(doc, id);
            lock (SnapshotByElementId)
            {
                if (!string.IsNullOrWhiteSpace(key)) SnapshotByElementId.TryGetValue(key, out snapshot);
                if (!string.IsNullOrWhiteSpace(key)) SnapshotByElementId.Remove(key);
            }

            if (ShouldIgnoreSnapshot(snapshot)) return;

            Queue.Enqueue(new ElementHistoryEvent
            {
                Ts = DateTime.UtcNow,
                Project = doc.ProjectInformation?.Name ?? "Projet",
                ModelGuid = doc.GetHashCode().ToString(CultureInfo.InvariantCulture),
                ModelKey = GetDocumentKey(doc),
                ElementId = id.GetIdValue(),
                UniqueId = "deleted:" + id.GetIdValue(),
                Category = snapshot.Category,
                Family = snapshot?.Family ?? string.Empty,
                TypeName = snapshot?.TypeName ?? string.Empty,
                ThumbnailPath = ResolveThumbnailPath(doc, snapshot?.TypeId ?? -1, snapshot?.Family, snapshot?.TypeName),
                Action = "delete",
                User = user,
                Tx = tx,
                Delta = BuildDeleteDelta(snapshot)
            });
        }




        private static Dictionary<string, object> BuildDeleteDelta(ElementSnapshot snapshot)
        {
            if (snapshot == null) return null;

            var includeDetailedMesh = CaptureDetailedDeletedMesh;
            var delta = new Dictionary<string, object>
            {
                ["deletedUniqueId"] = snapshot.UniqueId,
                ["lastKnown"] = snapshot.Location == null ? null : new { x = snapshot.Location.X, y = snapshot.Location.Y, z = snapshot.Location.Z },
                ["bboxMin"] = snapshot.BBoxMin == null ? null : new { x = snapshot.BBoxMin.X, y = snapshot.BBoxMin.Y, z = snapshot.BBoxMin.Z },
                ["bboxMax"] = snapshot.BBoxMax == null ? null : new { x = snapshot.BBoxMax.X, y = snapshot.BBoxMax.Y, z = snapshot.BBoxMax.Z },
                ["obbCorners"] = !includeDetailedMesh || snapshot.ObbCorners == null ? null : snapshot.ObbCorners.Select(pt => new { x = pt.X, y = pt.Y, z = pt.Z }).ToArray(),
                ["ghostMesh"] = !includeDetailedMesh || snapshot.GhostMesh == null ? null : new
                {
                    vertices = snapshot.GhostMesh.Vertices,
                    faces = snapshot.GhostMesh.Faces
                }
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
            if (IsAxisLineElement(element)) return true;
            if (element is FamilySymbol familySymbol)
            {
                if (element.Category != null && ShouldIgnoreCategory(element.Category)) return true;
                return !IsUsefulHistoryText(familySymbol.FamilyName) && !IsUsefulHistoryText(familySymbol.Name);
            }
            if (ShouldIgnoreCategory(element.Category)) return true;
            return false;
        }

        private static bool ShouldKeepSelectionSnapshotSimple(Element element)
        {
            return ShouldKeepDetailedSnapshotSimple(element);
        }

        private static bool ShouldKeepDetailedSnapshotSimple(Element element)
        {
            if (element == null) return true;
            if (element is ImportInstance || element is RevitLinkInstance) return true;

            var typeName = element.GetType().Name ?? string.Empty;
            if (typeName.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("Link", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var categoryName = element.Category?.Name ?? string.Empty;
            if (IsInsulationCategoryName(categoryName))
                return true;

            if (categoryName.IndexOf("CAD", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("DWG", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("IFC", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("NWD", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("RVT", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("link", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("lien", StringComparison.OrdinalIgnoreCase) >= 0
                || categoryName.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var name = element.Name ?? string.Empty;
            return name.IndexOf(".dwg", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(".nwd", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase) >= 0;
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
            if (IsAxisLineSnapshot(snapshot))
                return true;
            return false;
        }

        private static bool IsAxisLineElement(Element element)
        {
            if (element == null) return false;

            string typeName = null;
            try
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    typeName = (element.Document?.GetElement(typeId) as ElementType)?.Name;
            }
            catch
            {
            }

            return IsAxisLineText(element.Category?.Name)
                || IsAxisLineText(element.Name)
                || IsAxisLineText(typeName);
        }

        private static bool IsAxisLineSnapshot(ElementSnapshot snapshot)
        {
            return snapshot != null
                && (IsAxisLineText(snapshot.Category)
                    || IsAxisLineText(snapshot.Name)
                    || IsAxisLineText(snapshot.Family)
                    || IsAxisLineText(snapshot.TypeName));
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
            return name.Equals("Sheets", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Schedules", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Viewports", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Materials", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Line Styles", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Project Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RVT Links", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CAD Links", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInsulationCategoryName(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return false;
            var name = categoryName.Trim();
            return name.Equals("Pipe Insulations", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Pipe Insulation", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Duct Insulations", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Duct Insulation", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Isolants de canalisation", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Isolant de canalisation", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Isolation de canalisation", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Isolants de gaine", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Isolant de gaine", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Isolation de gaine", StringComparison.OrdinalIgnoreCase)
                || (name.IndexOf("insulation", StringComparison.OrdinalIgnoreCase) >= 0
                    && (name.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("duct", StringComparison.OrdinalIgnoreCase) >= 0))
                || (name.IndexOf("isol", StringComparison.OrdinalIgnoreCase) >= 0
                    && (name.IndexOf("canalisation", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("gaine", StringComparison.OrdinalIgnoreCase) >= 0));
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

        internal static string ResolveThumbnailPath(Document doc, int typeId, string family, string typeName)
        {
            var catalog = ResolveThumbnailPath(family, typeName);
            if (!string.IsNullOrWhiteSpace(catalog)) return catalog;

            var revit = TryCreateRevitTypeThumbnail(doc, typeId, family, typeName);
            if (!string.IsNullOrWhiteSpace(revit)) return revit;
            return null;
        }

        internal static string ResolveThumbnailPath(string family, string typeName)
        {
            var roots = GetConfiguredImageRoots();
            var key = "catalog-strict-family-v2|" + string.Join(";", roots) + "|" + (family ?? string.Empty).Trim() + "|" + (typeName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(family)) return null;
            if (ThumbnailPathCache.TryGetValue(key, out var cached))
                return string.IsNullOrWhiteSpace(cached) ? null : cached;

            if (roots.Count == 0) return null;

            foreach (var root in roots)
            {
                foreach (var name in BuildImageNameCandidates(family, typeName))
                {
                    foreach (var ext in ImageExtensions)
                    {
                        var path = Path.Combine(root, name + ext);
                        if (File.Exists(path) && ImageFileMatchesFamily(path, family))
                            return ThumbnailPathCache[key] = path;
                    }
                }
            }

            foreach (var root in roots)
            {
                var indexed = FindBestIndexedImage(root, family, typeName);
                if (!string.IsNullOrWhiteSpace(indexed))
                    return ThumbnailPathCache[key] = indexed;
            }

            return null;
        }

        internal static bool IsThumbnailPathValidForFamily(string path, string family)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            var normalizedPath = path.Replace('/', '\\');
            if (normalizedPath.IndexOf("\\CacheVignettes\\QuiAFaitCa\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return ImageFileMatchesFamily(path, family);
        }

        private static string TryCreateRevitTypeThumbnail(Document doc, int typeId, string family, string typeName)
        {
            if (doc == null || typeId <= 0) return null;

            var key = "revit|" + GetDocumentKey(doc) + "|" + typeId.ToString(CultureInfo.InvariantCulture);
            if (ThumbnailPathCache.TryGetValue(key, out var cached))
                return string.IsNullOrWhiteSpace(cached) ? null : cached;

            try
            {
                var type = doc.GetElement(new ElementId(typeId)) as ElementType;
                if (type == null)
                {
                    ThumbnailPathCache[key] = string.Empty;
                    return null;
                }

                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs",
                    "CacheVignettes",
                    "QuiAFaitCa");
                Directory.CreateDirectory(dir);

                var name = CleanFileName(FirstUsefulText(family, typeName, type.Name, "type_" + typeId.ToString(CultureInfo.InvariantCulture)));
                var path = Path.Combine(dir, typeId.ToString(CultureInfo.InvariantCulture) + "_" + name + ".png");
                if (File.Exists(path))
                    return ThumbnailPathCache[key] = path;

                using (var bmp = type.GetPreviewImage(new System.Drawing.Size(256, 256)))
                {
                    if (bmp == null)
                    {
                        ThumbnailPathCache[key] = string.Empty;
                        return null;
                    }

                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return ThumbnailPathCache[key] = path;
                }
            }
            catch
            {
                ThumbnailPathCache[key] = string.Empty;
                return null;
            }
        }

        private static IEnumerable<string> BuildImageNameCandidates(string family, string typeName)
        {
            foreach (var value in new[]
            {
                family,
                string.Join(" ", new[] { family, typeName }.Where(x => !string.IsNullOrWhiteSpace(x))),
                string.Join(" ", new[] { typeName, family }.Where(x => !string.IsNullOrWhiteSpace(x)))
            }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cleaned = CleanFileName(value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    yield return cleaned;
            }
        }

        private static string CleanFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Trim()
                .Where(ch => !invalid.Contains(ch))
                .ToArray();
            return new string(chars).Trim();
        }

        private static string FirstUsefulText(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string FindBestIndexedImage(string root, string family, string typeName)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            var index = ImageIndexCache.GetOrAdd(root, BuildImageIndex);
            if (index.Count == 0) return null;

            var familyKey = NormalizeImageKey(family);
            if (string.IsNullOrWhiteSpace(familyKey)) return null;
            var typeKey = NormalizeImageKey(typeName);
            var queries = BuildNormalizedImageQueries(family, typeName)
                .Where(q => !string.Equals(q, typeKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (queries.Count == 0) return null;

            ImageFileCandidate best = null;
            var bestScore = 0;
            foreach (var candidate in index)
            {
                var score = ScoreImageCandidate(candidate, familyKey, typeKey, queries);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return bestScore >= 95 ? best?.Path : null;
        }

        private static List<ImageFileCandidate> BuildImageIndex(string root)
        {
            var result = new List<ImageFileCandidate>();
            var dirs = new Stack<string>();
            try
            {
                if (!Directory.Exists(root)) return result;
                dirs.Push(root);
            }
            catch
            {
                return result;
            }

            while (dirs.Count > 0 && result.Count < MaxIndexedImageFiles)
            {
                var dir = dirs.Pop();

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { files = Array.Empty<string>(); }

                foreach (var file in files)
                {
                    if (result.Count >= MaxIndexedImageFiles) break;
                    var ext = Path.GetExtension(file);
                    if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                    var nameKey = NormalizeImageKey(Path.GetFileNameWithoutExtension(file));
                    if (string.IsNullOrWhiteSpace(nameKey)) continue;
                    result.Add(new ImageFileCandidate
                    {
                        Path = file,
                        NameKey = nameKey,
                        Tokens = new HashSet<string>(GetImageTokens(nameKey), StringComparer.OrdinalIgnoreCase)
                    });
                }

                string[] children;
                try { children = Directory.GetDirectories(dir); }
                catch { children = Array.Empty<string>(); }
                foreach (var child in children)
                    dirs.Push(child);
            }

            return result;
        }

        private static IEnumerable<string> BuildNormalizedImageQueries(string family, string typeName)
        {
            foreach (var value in new[]
            {
                family,
                string.Join(" ", new[] { family, typeName }.Where(x => !string.IsNullOrWhiteSpace(x))),
                string.Join(" ", new[] { typeName, family }.Where(x => !string.IsNullOrWhiteSpace(x)))
            })
            {
                var normalized = NormalizeImageKey(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }

        private static int ScoreImageCandidate(ImageFileCandidate candidate, string familyKey, string typeKey, List<string> queries)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.NameKey)) return 0;
            if (!ImageCandidateMatchesFamily(candidate, familyKey)) return 0;

            var score = 0;
            foreach (var query in queries)
            {
                if (candidate.NameKey.Equals(query, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 130);
                else if (query.Length >= 5 && candidate.NameKey.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    score = Math.Max(score, 110);
                else if (candidate.NameKey.Length >= 5 && query.IndexOf(candidate.NameKey, StringComparison.OrdinalIgnoreCase) >= 0)
                    score = Math.Max(score, 95);
            }

            var familyTokens = GetImageTokens(familyKey).ToList();
            var typeTokens = GetImageTokens(typeKey).ToList();
            var familyHits = familyTokens.Count(t => CandidateContainsToken(candidate, t));
            var typeHits = typeTokens.Count(t => CandidateContainsToken(candidate, t));

            if (familyTokens.Count > 0 && familyHits == familyTokens.Count)
                score = Math.Max(score, 95 + familyHits * 5);
            if (typeHits > 0 && familyHits > 0)
                score = Math.Max(score, 105 + typeHits * 7 + familyHits * 5);

            return score;
        }

        private static bool ImageFileMatchesFamily(string path, string family)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var candidate = new ImageFileCandidate
            {
                Path = path,
                NameKey = NormalizeImageKey(Path.GetFileNameWithoutExtension(path))
            };
            candidate.Tokens = new HashSet<string>(GetImageTokens(candidate.NameKey), StringComparer.OrdinalIgnoreCase);
            return ImageCandidateMatchesFamily(candidate, NormalizeImageKey(family));
        }

        private static bool ImageCandidateMatchesFamily(ImageFileCandidate candidate, string familyKey)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.NameKey) || string.IsNullOrWhiteSpace(familyKey))
                return false;

            if (candidate.NameKey.Equals(familyKey, StringComparison.OrdinalIgnoreCase))
                return true;
            if (candidate.NameKey.StartsWith(familyKey + " ", StringComparison.OrdinalIgnoreCase))
                return true;
            if (candidate.NameKey.EndsWith(" " + familyKey, StringComparison.OrdinalIgnoreCase))
                return true;
            if (candidate.NameKey.IndexOf(" " + familyKey + " ", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            var familyTokens = GetImageTokens(familyKey).ToList();
            return familyTokens.Count > 0 && familyTokens.All(t => candidate.Tokens.Contains(t));
        }

        private static bool CandidateContainsToken(ImageFileCandidate candidate, string token)
        {
            return !string.IsNullOrWhiteSpace(token)
                && (candidate.Tokens.Contains(token)
                    || candidate.NameKey.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<string> GetImageTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;
            foreach (var token in value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 3)
                    yield return token;
            }
        }

        private static string NormalizeImageKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else
                    sb.Append(' ');
            }

            return string.Join(" ", sb.ToString()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static List<string> GetConfiguredImageRoots()
        {
            try
            {
                var file = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitLogs",
                    "SauvegardePréférence",
                    "CheminsFamille.json");

                if (!File.Exists(file)) return new List<string>();
                var cfg = JsonConvert.DeserializeObject<FamilyFolderSettings>(File.ReadAllText(file));
                if (cfg == null) return new List<string>();

                var roots = new List<string>();
                AddImageRoot(roots, cfg.ImagesFolder);
                return roots;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void AddImageRoot(List<string> roots, string path)
        {
            if (roots == null || string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return;
                if (!roots.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(full);
            }
            catch
            {
            }
        }

        private sealed class FamilyFolderSettings
        {
            public string FamiliesFolder { get; set; }
            public string ImagesFolder { get; set; }
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
                var savedKey = (central ?? doc.PathName ?? string.Empty).Trim();
                return !string.IsNullOrWhiteSpace(savedKey)
                    ? savedKey
                    : GetRuntimeDocumentKey(doc);
            }
            catch { return GetRuntimeDocumentKey(doc); }
        }

        private static string GetRuntimeDocumentKey(Document doc)
        {
            if (doc == null) return string.Empty;

            var hash = doc.GetHashCode();
            return RuntimeDocumentKeys.GetOrAdd(hash, _ =>
            {
                var title = string.Empty;
                try { title = (doc.Title ?? string.Empty).Trim(); }
                catch { }

                if (string.IsNullOrWhiteSpace(title))
                    title = "Projet sans nom";

                return "unsaved|" + RuntimeSessionId + "|" +
                       hash.ToString(CultureInfo.InvariantCulture) + "|" +
                       title;
            });
        }

        internal static string GetDocumentKeyForHistory(Document doc)
        {
            return GetDocumentKey(doc);
        }

        internal static List<string> GetDocumentKeysForHistory(Document doc)
        {
            var keys = new List<string>();
            if (doc == null) return keys;

            AddDocumentKeyCandidate(keys, GetDocumentKey(doc));

            try
            {
                if (doc.IsWorkshared)
                {
                    var mp = doc.GetWorksharingCentralModelPath();
                    if (mp != null)
                        AddDocumentKeyCandidate(keys, ModelPathUtils.ConvertModelPathToUserVisiblePath(mp));
                }
            }
            catch
            {
            }

            try { AddDocumentKeyCandidate(keys, doc.PathName); }
            catch { }

            try { AddDocumentKeyCandidate(keys, doc.Title); }
            catch { }

            return keys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddDocumentKeyCandidate(List<string> keys, string value)
        {
            if (keys == null || string.IsNullOrWhiteSpace(value)) return;
            var key = value.Trim();
            if (key.Length == 0) return;
            if (!keys.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)))
                keys.Add(key);
        }

        private static DateTime GetHistoryFileSortDate(string path)
        {
            var token = GetHistoryDateToken(path);
            if (DateTime.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return day.Date.AddDays(1).AddTicks(-1);
            if (DateTime.TryParseExact(token, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                return month.Date;

            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private static bool IsDeadlineExpired(DateTime deadlineUtc)
        {
            return deadlineUtc != DateTime.MaxValue && DateTime.UtcNow >= deadlineUtc;
        }

        private static List<XYZ> GetOrientedCorners(Element el, DateTime deadlineUtc)
        {
            try
            {
                if (IsDeadlineExpired(deadlineUtc)) return null;
                var opts = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                var ge = el.get_Geometry(opts);
                if (IsDeadlineExpired(deadlineUtc)) return null;
                var pts = new List<XYZ>();
                CollectPoints(ge, pts, deadlineUtc);
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

        private static void CollectPoints(GeometryElement ge, List<XYZ> pts, DateTime deadlineUtc)
        {
            if (ge == null) return;
            foreach (var go in ge)
            {
                if (IsDeadlineExpired(deadlineUtc)) return;
                if (go is Solid s && s.Faces.Size > 0)
                {
                    foreach (Face f in s.Faces)
                    {
                        if (IsDeadlineExpired(deadlineUtc)) return;
                        var m = f.Triangulate();
                        for (int i = 0; i < m.Vertices.Count; i++) pts.Add(m.Vertices[i]);
                    }
                }
                else if (go is GeometryInstance gi)
                {
                    if (IsDeadlineExpired(deadlineUtc)) return;
                    CollectPoints(gi.GetInstanceGeometry(), pts, deadlineUtc);
                }
            }
        }

        private static GhostMeshSnapshot CaptureGhostMesh(Element el, DateTime deadlineUtc)
        {
            GhostMeshSnapshot bestDecimated = null;

            foreach (var detailLevel in new[] { ViewDetailLevel.Fine, ViewDetailLevel.Medium, ViewDetailLevel.Coarse })
            {
                if (IsDeadlineExpired(deadlineUtc)) break;
                var snapshot = CaptureGhostMesh(el, detailLevel, deadlineUtc, out var decimated);
                if (snapshot == null) continue;
                if (!decimated) return snapshot;
                bestDecimated = snapshot;
            }

            return bestDecimated;
        }

        private static GhostMeshSnapshot CaptureGhostMesh(Element el, ViewDetailLevel detailLevel, DateTime deadlineUtc, out bool decimated)
        {
            decimated = false;
            try
            {
                if (el == null || IsDeadlineExpired(deadlineUtc)) return null;
                var opts = new Options
                {
                    ComputeReferences = false,
                    DetailLevel = detailLevel,
                    IncludeNonVisibleObjects = false
                };
                var ge = el.get_Geometry(opts);
                if (IsDeadlineExpired(deadlineUtc)) return null;
                var builder = new GhostMeshBuilder();
                CollectGhostMesh(ge, builder, deadlineUtc);

                return builder.ToSnapshot(out decimated);
            }
            catch
            {
                return null;
            }
        }

        private static void CollectGhostMesh(GeometryElement ge, GhostMeshBuilder builder, DateTime deadlineUtc)
        {
            if (ge == null || builder == null) return;

            foreach (var go in ge)
            {
                if (IsDeadlineExpired(deadlineUtc)) return;
                if (go is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            if (IsDeadlineExpired(deadlineUtc)) return;
                            var mesh = face.Triangulate();
                            if (mesh == null) continue;

                            for (int i = 0; i < mesh.NumTriangles; i++)
                            {
                                if (IsDeadlineExpired(deadlineUtc)) return;
                                var triangle = mesh.get_Triangle(i);
                                var a = triangle.get_Vertex(0);
                                var b = triangle.get_Vertex(1);
                                var c = triangle.get_Vertex(2);
                                builder.AddTriangle(a, b, c);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                else if (go is GeometryInstance gi)
                {
                    try
                    {
                        if (IsDeadlineExpired(deadlineUtc)) return;
                        CollectGhostMesh(gi.GetInstanceGeometry(), builder, deadlineUtc);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool IsUsefulTriangle(XYZ a, XYZ b, XYZ c)
        {
            if (a == null || b == null || c == null) return false;
            var ab = b - a;
            var ac = c - a;
            return ab.CrossProduct(ac).GetLength() > 1e-8;
        }

        private static double GetTriangleArea(XYZ a, XYZ b, XYZ c)
        {
            if (a == null || b == null || c == null) return 0;
            return ((b - a).CrossProduct(c - a).GetLength()) * 0.5;
        }

        private static string BuildGhostVertexKey(XYZ point)
        {
            return RoundGhostCoord(point.X).ToString("G17", CultureInfo.InvariantCulture) + "|" +
                   RoundGhostCoord(point.Y).ToString("G17", CultureInfo.InvariantCulture) + "|" +
                   RoundGhostCoord(point.Z).ToString("G17", CultureInfo.InvariantCulture);
        }

        private static double RoundGhostCoord(double value)
        {
            return Math.Round(value, GhostCoordinateDecimals);
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
            var nextMaintenanceUtc = DateTime.UtcNow.Add(HistoryMaintenanceInitialDelay);

            while (!token.IsCancellationRequested)
            {
                Flush();

                if (DateTime.UtcNow >= nextMaintenanceUtc)
                {
                    CompressOldFiles();
                    nextMaintenanceUtc = DateTime.UtcNow.Add(HistoryMaintenanceInterval);
                }

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
            lock (FileSync)
            {
                foreach (var file in Directory.GetFiles(dir, "element-history-*.jsonl"))
                {
                    try
                    {
                        var dt = TryGetDailyHistoryDate(file) ?? File.GetLastWriteTimeUtc(file);
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalDays < 7) continue;

                        var archive = GetMonthlyArchivePath(dir, dt);
                        AppendHistoryFileToArchive(file, archive);
                        File.Delete(file);
                    }
                    catch { }
                }

                ConsolidateDailyGzipArchives(dir);
            }
        }

        private static void ConsolidateDailyGzipArchives(string dir)
        {
            foreach (var file in Directory.GetFiles(dir, "element-history-*.jsonl.gz"))
            {
                try
                {
                    var dt = TryGetDailyHistoryDate(file);
                    if (dt == null) continue;

                    var archive = GetMonthlyArchivePath(dir, dt.Value);
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase))
                        continue;

                    AppendHistoryFileToArchive(file, archive);
                    File.Delete(file);
                }
                catch { }
            }
        }

        private static void AppendHistoryFileToArchive(string sourcePath, string archivePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return;
            if (string.IsNullOrWhiteSpace(archivePath)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath));
            var tempPath = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var dst = File.Create(tempPath))
                using (var gzip = new GZipStream(dst, CompressionLevel.Optimal))
                using (var writer = new StreamWriter(gzip, Encoding.UTF8))
                {
                    if (File.Exists(archivePath))
                    {
                        foreach (var line in ReadHistoryLines(archivePath))
                            writer.WriteLine(line);
                    }

                    foreach (var line in ReadHistoryLines(sourcePath))
                        writer.WriteLine(line);
                }

                ReplaceFile(tempPath, archivePath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch { }
            }
        }

        private static void ReplaceFile(string tempPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                var backup = destinationPath + ".bak";
                try
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(tempPath, destinationPath, backup);
                    if (File.Exists(backup)) File.Delete(backup);
                    return;
                }
                catch
                {
                    try
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                    catch { }
                }

                var fallbackBackup = destinationPath + "." + Guid.NewGuid().ToString("N") + ".bak";
                File.Move(destinationPath, fallbackBackup);
                try
                {
                    File.Move(tempPath, destinationPath);
                    File.Delete(fallbackBackup);
                    return;
                }
                catch
                {
                    if (!File.Exists(destinationPath) && File.Exists(fallbackBackup))
                        File.Move(fallbackBackup, destinationPath);
                    throw;
                }
            }

            File.Move(tempPath, destinationPath);
        }

        private static string GetMonthlyArchivePath(string dir, DateTime date)
        {
            return Path.Combine(dir, $"element-history-{date:yyyy-MM}.jsonl.gz");
        }

        private static DateTime? TryGetDailyHistoryDate(string path)
        {
            var token = GetHistoryDateToken(path);
            if (DateTime.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
            return null;
        }

        private static string GetHistoryDateToken(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            if (!name.StartsWith("element-history-", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var token = name.Substring("element-history-".Length);
            if (token.EndsWith(".jsonl.gz", StringComparison.OrdinalIgnoreCase))
                return token.Substring(0, token.Length - ".jsonl.gz".Length);
            if (token.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                return token.Substring(0, token.Length - ".jsonl".Length);
            return token;
        }
    }
}

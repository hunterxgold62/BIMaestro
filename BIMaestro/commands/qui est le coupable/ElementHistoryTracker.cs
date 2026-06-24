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
            public List<List<XYZ>> GhostFaces { get; set; }
            public Dictionary<string, string> Parameters { get; set; }
            public DateTime LastLogged { get; set; }
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
        private static readonly ConcurrentDictionary<int, string> RuntimeDocumentKeys =
            new ConcurrentDictionary<int, string>();
        private static readonly string RuntimeSessionId = Guid.NewGuid().ToString("N");
        private static CancellationTokenSource _cts;
        private static Task _worker;
        private const double MinMoveFeet = 0.00656168; // ~2 mm
        private const int DefaultElementHistoryTake = 1000;
        private const int DefaultModelHistoryTake = 1000;
        private const int DefaultDeletedHistoryTake = 1000;
        private const int MaxIndexedImageFiles = 12000;
        private const int MaxGhostPreviewFaces = 160;
        private const bool IncludeAnnotationCategoriesForFuture = false;
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

        internal static void FlushPendingForHistory()
        {
            try { Flush(); }
            catch { }
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
        }

        private static void PrimeElementSnapshot(Element el)
        {
            try
            {
                if (el == null || ShouldIgnoreElement(el)) return;
                var snapshot = BuildSnapshot(el, includeOrientedCorners: true);
                StoreSnapshot(el.Document, el.Id, snapshot);
            }
            catch
            {
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
            var wasPrimed = IsDocumentPrimed(doc);

            var addedIds = e.GetAddedElementIds().ToList();
            var modifiedIds = e.GetModifiedElementIds().ToList();
            var deletedIds = e.GetDeletedElementIds().ToList();
            var suppressSecondaryModifications = deletedIds.Count > 0;

            foreach (var id in addedIds)
                CaptureAddedOrModified(doc, id, user, tx, isCreate: true);
            foreach (var id in modifiedIds.OrderBy(id => IsFamilySymbolElementId(doc, id) ? 1 : 0))
                CaptureAddedOrModified(doc, id, user, tx, isCreate: false, suppressSecondaryModification: suppressSecondaryModifications);
            foreach (var id in deletedIds)
                EnqueueDeleted(doc, id, user, tx);

            CaptureFamilyDocumentTypeChanges(doc, user, tx);
            CaptureProjectFamilySymbolChanges(doc, user, tx);

            if (!wasPrimed)
                PrimeDocument(doc);
        }

        public static List<ElementHistoryEvent> LoadElementHistory(Document doc, Element element)
        {
            var uniqueId = element?.UniqueId;
            if (string.IsNullOrWhiteSpace(uniqueId)) return new List<ElementHistoryEvent>();
            var key = GetDocumentKey(doc);
            return LoadElementHistory(key, uniqueId, DefaultElementHistoryTake);
        }

        internal static List<ElementHistoryEvent> LoadElementHistory(string modelKey, string uniqueId, int take)
        {
            if (string.IsNullOrWhiteSpace(uniqueId)) return new List<ElementHistoryEvent>();
            return LoadMatchingHistory(modelKey, uniqueId, null, take);
        }


        public static List<ElementHistoryEvent> LoadRecentModelHistory(Document doc, int take = DefaultModelHistoryTake)
        {
            var key = GetDocumentKey(doc);
            return LoadRecentModelHistory(key, take);
        }

        internal static List<ElementHistoryEvent> LoadRecentModelHistory(string modelKey, int take = DefaultModelHistoryTake)
        {
            return LoadMatchingHistory(modelKey, null, null, take);
        }

        public static List<ElementHistoryEvent> LoadRecentDeletedHistory(Document doc)
        {
            var key = GetDocumentKey(doc);
            return LoadMatchingHistory(key, null, "delete", DefaultDeletedHistoryTake);
        }

        internal static bool IsDisplayableHistoryEvent(ElementHistoryEvent ev)
        {
            if (ev == null) return false;
            if (IsLowValueParameterChange(ev)) return false;
            return IsUsefulHistoryText(ev.Category);
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
            if (string.IsNullOrWhiteSpace(modelKey))
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
                        if (!MatchesHistoryFilter(ev, modelKey, uniqueId, action)) continue;
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
            if (ev == null) return false;
            if (!string.IsNullOrWhiteSpace(modelKey)
                && !string.Equals(ev.ModelKey ?? string.Empty, modelKey, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(uniqueId)
                && !string.Equals(ev.UniqueId ?? string.Empty, uniqueId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(action)
                && !string.Equals(ev.Action ?? string.Empty, action, StringComparison.OrdinalIgnoreCase))
                return false;
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

        private static void CaptureAddedOrModified(Document doc, ElementId id, string user, string tx, bool isCreate, bool suppressSecondaryModification = false)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            var el = doc.GetElement(id);
            if (el == null || ShouldIgnoreElement(el)) return;

            Dictionary<string, object> relatedTypeParameterDelta = null;
            if (!isCreate && !(el is FamilySymbol))
                relatedTypeParameterDelta = CaptureRelatedFamilyTypeParameterDelta(doc, el);

            var current = BuildSnapshot(el, includeOrientedCorners: true);
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
                return;
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

        private static Dictionary<string, object> CaptureRelatedFamilyTypeParameterDelta(Document doc, Element el)
        {
            try
            {
                var typeId = el.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) return null;

                var type = doc.GetElement(typeId) as FamilySymbol;
                if (type == null || ShouldIgnoreElement(type)) return null;

                var current = BuildSnapshot(type, includeOrientedCorners: false);
                ElementSnapshot previous;
                lock (SnapshotByElementId)
                {
                    SnapshotByElementId.TryGetValue(BuildElementSnapshotKey(doc, type.Id), out previous);
                }

                var action = DetermineAction(previous, current);
                var delta = BuildDelta(action, previous, current);
                StoreSnapshot(doc, type.Id, current);

                return string.Equals(action, "param_change", StringComparison.OrdinalIgnoreCase)
                       && HasParameterDelta(delta)
                    ? delta
                    : null;
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

        private static ElementSnapshot BuildSnapshot(Element el, bool includeOrientedCorners)
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

            return new ElementSnapshot
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
                ObbCorners = includeOrientedCorners ? GetOrientedCorners(el) : null,
                GhostFaces = includeOrientedCorners ? CaptureGhostFaces(el) : null,
                Parameters = parameters
            };
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

            var delta = new Dictionary<string, object>
            {
                ["deletedUniqueId"] = snapshot.UniqueId,
                ["lastKnown"] = snapshot.Location == null ? null : new { x = snapshot.Location.X, y = snapshot.Location.Y, z = snapshot.Location.Z },
                ["bboxMin"] = snapshot.BBoxMin == null ? null : new { x = snapshot.BBoxMin.X, y = snapshot.BBoxMin.Y, z = snapshot.BBoxMin.Z },
                ["bboxMax"] = snapshot.BBoxMax == null ? null : new { x = snapshot.BBoxMax.X, y = snapshot.BBoxMax.Y, z = snapshot.BBoxMax.Z },
                ["obbCorners"] = snapshot.ObbCorners == null ? null : snapshot.ObbCorners.Select(pt => new { x = pt.X, y = pt.Y, z = pt.Z }).ToArray(),
                ["ghostFaces"] = snapshot.GhostFaces == null ? null : snapshot.GhostFaces
                    .Select(face => face.Select(pt => new { x = pt.X, y = pt.Y, z = pt.Z }).ToArray())
                    .ToArray()
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
            if (element is FamilySymbol familySymbol)
            {
                if (element.Category != null && ShouldIgnoreCategory(element.Category)) return true;
                return !IsUsefulHistoryText(familySymbol.FamilyName) && !IsUsefulHistoryText(familySymbol.Name);
            }
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
            return name.Equals("Sheets", StringComparison.OrdinalIgnoreCase)
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

        private static List<List<XYZ>> CaptureGhostFaces(Element el)
        {
            try
            {
                if (el == null) return null;

                var opts = new Options
                {
                    ComputeReferences = false,
                    DetailLevel = ViewDetailLevel.Coarse,
                    IncludeNonVisibleObjects = false
                };
                var ge = el.get_Geometry(opts);
                var faces = new List<List<XYZ>>();
                CollectGhostFaces(ge, faces);

                if (faces.Count == 0 || faces.Count > MaxGhostPreviewFaces)
                    return null;

                return faces;
            }
            catch
            {
                return null;
            }
        }

        private static void CollectGhostFaces(GeometryElement ge, List<List<XYZ>> faces)
        {
            if (ge == null || faces == null || faces.Count > MaxGhostPreviewFaces) return;

            foreach (var go in ge)
            {
                if (faces.Count > MaxGhostPreviewFaces) return;

                if (go is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (faces.Count > MaxGhostPreviewFaces) return;

                        try
                        {
                            var mesh = face.Triangulate();
                            if (mesh == null) continue;

                            for (int i = 0; i < mesh.NumTriangles; i++)
                            {
                                var triangle = mesh.get_Triangle(i);
                                var a = triangle.get_Vertex(0);
                                var b = triangle.get_Vertex(1);
                                var c = triangle.get_Vertex(2);
                                if (!IsUsefulTriangle(a, b, c)) continue;

                                faces.Add(new List<XYZ> { a, b, c });
                                if (faces.Count > MaxGhostPreviewFaces) return;
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
                        CollectGhostFaces(gi.GetInstanceGeometry(), faces);
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

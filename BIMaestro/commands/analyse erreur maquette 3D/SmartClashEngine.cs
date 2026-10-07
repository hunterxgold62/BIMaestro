using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Analyse
{
    public enum SmartScanScope { Model, ActiveView, Selection }
    public sealed class SmartLinkChoice
    {
        public ElementId Id { get; set; }
        public string Name { get; set; }
        public bool Loaded { get; set; }
        public bool Selected { get; set; }
        public string Label => Name + (Loaded ? "" : " — non chargé");
    }
    public sealed class SmartScanSetup
    {
        public Document Document { get; set; }
        public string DocumentKey { get; set; }
        public string Title { get; set; }
        public ElementId ViewId { get; set; }
        public bool ViewSupported { get; set; }
        public List<ElementId> Selection { get; set; } = new List<ElementId>();
        public List<SmartLinkChoice> Links { get; set; } = new List<SmartLinkChoice>();
        public static SmartScanSetup Capture(UIDocument ui)
        {
            var doc = ui.Document;
            return new SmartScanSetup
            {
                Document = doc, DocumentKey = SmartCheckState.GetDocKey(doc), Title = doc.Title,
                ViewId = ui.ActiveView.Id,
                ViewSupported = FilteredElementCollector.IsViewValidForElementIteration(doc, ui.ActiveView.Id),
                Selection = ui.Selection.GetElementIds().ToList(),
                Links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Select(l => new SmartLinkChoice { Id = l.Id, Name = l.Name, Loaded = l.GetLinkDocument() != null,
                        Selected = l.GetLinkDocument() != null }).OrderBy(l => l.Name).ToList()
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                        .Where(l => !l.ViewSpecific).Select(l => new SmartLinkChoice { Id = l.Id, Name = l.Name,
                            Loaded = true, Selected = true })).ToList()
            };
        }
    }
    public sealed class SmartScanOptions
    {
        public SmartScanScope Scope { get; set; }
        public bool Pipes { get; set; } = true;
        public bool Ducts { get; set; } = true;
        public bool CableTrays { get; set; } = true;
        public bool Conduits { get; set; } = true;
        public bool Fittings { get; set; } = true;
        public bool Equipment { get; set; }
        public bool GenericModels { get; set; }
        public bool LocalClashes { get; set; } = true;
        public bool LinkedClashes { get; set; } = true;
        public bool OpenConnectors { get; set; }
        public bool WallSupports { get; set; }
        public bool IncludeInsulation { get; set; } = true;
        public double MinimumVolumeMm3 { get; set; } = 10;
        [Newtonsoft.Json.JsonIgnore]
        public List<ElementId> LinkIds { get; set; } = new List<ElementId>();
        [Newtonsoft.Json.JsonIgnore]
        public string Signature => string.Join("|", new object[] { Scope, Pipes, Ducts, CableTrays, Conduits, Fittings,
            Equipment, GenericModels, LocalClashes, LinkedClashes, OpenConnectors, WallSupports, IncludeInsulation,
            MinimumVolumeMm3.ToString("R", CultureInfo.InvariantCulture), string.Join(",", LinkIds.Select(x => x.GetIdLongValue()).OrderBy(x => x)) });
        public List<BuiltInCategory> SourceCategories()
        {
            var result = new List<BuiltInCategory>();
            if (Pipes) { result.Add(BuiltInCategory.OST_PipeCurves); result.Add(BuiltInCategory.OST_FlexPipeCurves);
                if (Fittings) result.AddRange(new[] { BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory }); }
            if (Ducts) { result.Add(BuiltInCategory.OST_DuctCurves); result.Add(BuiltInCategory.OST_FlexDuctCurves);
                if (Fittings) result.AddRange(new[] { BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory }); }
            if (CableTrays) { result.Add(BuiltInCategory.OST_CableTray); if (Fittings) result.Add(BuiltInCategory.OST_CableTrayFitting); }
            if (Conduits) { result.Add(BuiltInCategory.OST_Conduit); if (Fittings) result.Add(BuiltInCategory.OST_ConduitFitting); }
            if (Equipment) result.AddRange(new[] { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_DuctTerminal });
            if (GenericModels) result.Add(BuiltInCategory.OST_GenericModel);
            return result.Distinct().ToList();
        }
    }

    // Advanced only in a Revit API callback. Each slice returns control to Revit and WPF.
    public sealed class SmartScanSession : IDisposable
    {
        private readonly SmartScanSetup _setup;
        private readonly SmartScanOptions _options;
        private readonly IEnumerator<bool> _work;
        private readonly Dictionary<string, GeometryData> _geometry = new Dictionary<string, GeometryData>();
        private readonly HashSet<string> _pairs = new HashSet<string>();
        private readonly HashSet<string> _warnings = new HashSet<string>();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly SmartVisualCapture _visuals = new SmartVisualCapture();
        private readonly Queue<VisualWork> _pendingVisuals = new Queue<VisualWork>();
        private bool _detectionComplete;
        private sealed class VisualWork
        {
            internal ModelIssue Issue;
            internal GeometryData Source, Target;
        }
        private readonly Options _geometryOptions = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
        public List<ModelIssue> Issues { get; } = new List<ModelIssue>();
        public List<string> Diagnostics { get; } = new List<string>();
        public string Stage { get; private set; } = "Préparation";
        public int SourceCount { get; private set; }
        public int ProcessedSources { get; private set; }
        public int CandidateCount { get; private set; }
        public int TestedPairs { get; private set; }
        public bool CancelRequested { get; set; }
        public bool Complete { get; private set; }
        public bool Cancelled { get; private set; }
        public string Error { get; private set; }
        public double Seconds => _elapsed.Elapsed.TotalSeconds;
        public double Progress => SourceCount == 0 ? 0 : 100.0 * ProcessedSources / SourceCount;
        public SmartScanOptions Options => _options;
        public SmartScanSession(SmartScanSetup setup, SmartScanOptions options)
        { _setup = setup; _options = options; _work = Run().GetEnumerator(); }
        public void Advance(int milliseconds = 75)
        {
            if (Complete) return;
            var slice = Stopwatch.StartNew();
            try
            {
                if (CancelRequested) { Cancelled = true; Finish(); return; }
                if (!_setup.Document.IsValidObject) throw new InvalidOperationException("La maquette a été fermée.");
                // Only prepare previews queued by an earlier callback: rows are published before triangulation.
                var visualBudget = _detectionComplete ? milliseconds : Math.Min(20, milliseconds);
                while (_pendingVisuals.Count > 0)
                {
                    var work = _pendingVisuals.Dequeue();
                    try { work.Issue.VisualScene = Visual(work.Source, work.Target, work.Issue.BBox, work.Issue.IsApproximate); }
                    catch (Exception ex) { Warn("visual:" + work.Issue.IssueKey, "Aperçu non préparé : " + ex.Message); }
                    finally { work.Issue.VisualPending = false; }
                    if (CancelRequested) { Cancelled = true; Finish(); return; }
                    if (slice.ElapsedMilliseconds >= visualBudget) break;
                }
                if (_detectionComplete)
                {
                    if (_pendingVisuals.Count == 0) { Stage = SourceCount == 0 ? "Aucun objet dans les catégories et le périmètre choisis" : "Analyse terminée"; Finish(); }
                    return;
                }
                if (milliseconds > 0 && slice.ElapsedMilliseconds >= milliseconds || _pendingVisuals.Count >= 32) return;
                do
                {
                    if (CancelRequested) { Cancelled = true; Finish(); return; }
                    if (!_setup.Document.IsValidObject) throw new InvalidOperationException("La maquette a été fermée.");
                    if (!_work.MoveNext())
                    {
                        _detectionComplete = true;
                        if (_pendingVisuals.Count == 0) Finish();
                        else Stage = "Préparation des derniers aperçus";
                        return;
                    }
                    // Bound publication and pending native geometry work even on very dense models.
                    if (_pendingVisuals.Count >= 32) return;
                } while (slice.ElapsedMilliseconds < milliseconds);
            }
            catch (Exception ex) { Error = ex.Message; Finish(); }
        }
        private void Finish()
        {
            Complete = true; _elapsed.Stop(); _work.Dispose();
            foreach (var work in _pendingVisuals) work.Issue.VisualPending = false;
            _pendingVisuals.Clear(); _geometry.Clear();
        }
        private void AddIssue(ModelIssue issue, GeometryData source, GeometryData target = null)
        {
            issue.VisualPending = true; Issues.Add(issue);
            _pendingVisuals.Enqueue(new VisualWork { Issue = issue, Source = source, Target = target });
        }
        public void Dispose() { CancelRequested = true; if (!Complete) Finish(); }
        private void Warn(string key, string message) { if (_warnings.Add(key)) Diagnostics.Add(message); }
        private IEnumerable<bool> Run()
        {
            var doc = _setup.Document;
            var categories = _options.SourceCategories();
            if (categories.Count == 0 && !_options.WallSupports) throw new InvalidOperationException("Choisissez au moins une catégorie à analyser.");
            if (_options.Scope == SmartScanScope.Selection && _setup.Selection.Count == 0)
                throw new InvalidOperationException("La sélection est vide. Sélectionnez des objets dans Revit puis actualisez le périmètre.");
            if (_options.Scope == SmartScanScope.ActiveView && !_setup.ViewSupported)
                throw new InvalidOperationException("Cette vue ne permet pas une analyse. Choisissez la maquette ou une sélection.");
            var sources = new List<Entry>();
            Stage = "Collecte des objets à analyser";
            foreach (var e in categories.Count == 0 ? Enumerable.Empty<Element>() : ScopedCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(categories)))
            {
                var entry = CreateEntry(e, Transform.Identity, null);
                if (entry != null) sources.Add(entry);
                SourceCount = sources.Count;
                yield return true;
            }
            SourceCount = sources.Count;
            var targetCategories = new SmartScanOptions { Equipment = true, GenericModels = true }.SourceCategories().Concat(new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Stairs }).Distinct().ToList();
            var index = new SpatialIndex();
            if (_options.LocalClashes && SourceCount > 0)
            {
                Stage = "Préparation de la maquette";
                var collector = _options.Scope == SmartScanScope.ActiveView
                    ? new FilteredElementCollector(doc, _setup.ViewId) : new FilteredElementCollector(doc);
                foreach (var e in collector.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(targetCategories)))
                {
                    var entry = CreateEntry(e, Transform.Identity, null);
                    if (entry != null) { index.Add(entry); CandidateCount++; }
                    yield return true;
                }
            }
            if (_options.LinkedClashes && SourceCount > 0)
            {
                foreach (var unavailable in _setup.Links.Where(l => !l.Loaded))
                    Warn("unloaded:" + unavailable.Id, "Lien non analysé car non chargé : " + unavailable.Name);
                if (_options.LinkIds.Count == 0)
                    Warn("no-links", "Aucun lien ni import sélectionné : les collisions externes n'ont pas été contrôlées.");
                var sourceBox = SmartGeometry.Union(sources.Select(e => e.Box));
                foreach (var id in _options.LinkIds)
                {
                    var linkElement = doc.GetElement(id);
                    Stage = "Préparation du lien : " + linkElement?.Name;
                    if (linkElement is RevitLinkInstance link)
                    {
                        var linkedDoc = link.GetLinkDocument();
                        if (linkedDoc == null) { Warn("link:" + id, "Lien non chargé : " + link.Name); continue; }
                        foreach (var e in new FilteredElementCollector(linkedDoc).WhereElementIsNotElementType()
                            .WherePasses(new ElementMulticategoryFilter(targetCategories)))
                        {
                            var entry = CreateEntry(e, link.GetTotalTransform(), link);
                            if (entry != null && SmartGeometry.Overlap(entry.Box, sourceBox)) { index.Add(entry); CandidateCount++; }
                            yield return true;
                        }
                    }
                    else if (linkElement is ImportInstance import)
                    {
                        var entry = CreateEntry(import, Transform.Identity, import);
                        if (entry != null && SmartGeometry.Overlap(entry.Box, sourceBox)) { index.Add(entry); CandidateCount++; }
                        yield return true;
                    }
                    else Warn("link:" + id, "Lien supprimé depuis l'ouverture de la fenêtre.");
                }
            }
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            foreach (var source in sources)
            {
                Stage = "Recherche des conflits";
                var sourceGeometry = _options.LocalClashes || _options.LinkedClashes ? Geometry(source) : new GeometryData();
                var connections = ConnectedIds(source.Element);
                foreach (var target in index.Query(source.Box))
                {
                    if (source.Key == target.Key || target.Link == null && connections.Contains(target.Element.Id.GetIdLongValue())) continue;
                    var key = PairKey(source.Key, target.Key);
                    if (!_pairs.Add(key)) continue;
                    TestedPairs++;
                    var targetGeometry = Geometry(target);
                    if (sourceGeometry.Solids.Count == 0 && sourceGeometry.MeshBoxes.Count == 0 && !sourceGeometry.Approximate
                        || targetGeometry.Solids.Count == 0 && targetGeometry.MeshBoxes.Count == 0 && !targetGeometry.Approximate)
                    { yield return true; continue; }
                    var hit = SmartGeometry.Intersection(sourceGeometry.Solids, targetGeometry.Solids, source.Box, target.Box,
                        _options.MinimumVolumeMm3 / Math.Pow(304.8, 3), sourceGeometry.Approximate || targetGeometry.Approximate);
                    if (hit.Failed) Warn("boolean:" + key, "Intersection géométrique non résolue : " + Label(source.Element) + " / " + Label(target.Element));
                    if (hit.Box != null)
                    {
                        var wall = target.Element is Wall;
                        var issue = NewIssue(source, target, hit.Box, levels);
                        issue.Kind = target.Link != null ? IssueKind.LinkPipeClash : wall ? IssueKind.MepThroughWallNoSleeve : IssueKind.LocalClash;
                        issue.Category = wall ? "Intersection réseau / paroi" : "Collision";
                        issue.IsApproximate = !hit.Confirmed;
                        issue.IntersectionVolumeMm3 = hit.Volume * Math.Pow(304.8, 3);
                        issue.Message = hit.Confirmed ? "Intersection des volumes physiques confirmée."
                            : "Les volumes englobants se chevauchent. Vérifiez les objets en 3D ; la collision physique n'est pas confirmée.";
                        var sourceFingerprint = SmartGeometry.Fingerprint(source.Box, 0, false) + "|" + issue.ElementTypeName;
                        var targetFingerprint = SmartGeometry.Fingerprint(target.Box, 0, false) + "|" + issue.RelatedTypeName;
                        issue.Fingerprint = SmartGeometry.Fingerprint(issue.BBox, issue.IntersectionVolumeMm3, issue.IsApproximate)
                            + "|" + (string.CompareOrdinal(source.Key, target.Key) <= 0 ? sourceFingerprint + "|" + targetFingerprint
                                : targetFingerprint + "|" + sourceFingerprint);
                        AddIssue(issue, sourceGeometry, targetGeometry);
                    }
                    yield return true;
                }
                if (_options.OpenConnectors)
                {
                    var open = OpenConnectorCount(source.Element);
                    if (open > 0)
                    {
                        var issue = NewIssue(source, null, source.Box, levels);
                        issue.Kind = IssueKind.MepUnconnected; issue.Category = "Connecteurs ouverts";
                        issue.Message = open + " connecteur(s) de terminaison ouvert(s). Une extrémité libre peut être volontaire.";
                        issue.Fingerprint = SmartGeometry.Fingerprint(issue.BBox, open, false); AddIssue(issue, Geometry(source));
                    }
                }
                ProcessedSources++; yield return true;
            }
            if (_options.WallSupports)
            {
                Stage = "Vérification indicative des supports de murs";
                var supports = new List<BoundingBoxXYZ>();
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(
                    new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation })))
                { var b = SmartGeometry.WorldBox(e.get_BoundingBox(null), Transform.Identity); if (b != null) supports.Add(b); yield return true; }
                foreach (var w in ScopedCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    var entry = CreateEntry(w, Transform.Identity, null); if (entry == null) continue;
                    var b = entry.Box; double tolerance = 20 / 304.8;
                    bool support = supports.Any(s => s.Min.Z < b.Min.Z - tolerance && s.Max.Z >= b.Min.Z - tolerance
                        && s.Max.Z <= b.Min.Z + 100 / 304.8 && SmartGeometry.OverlapXY(b, s));
                    if (!support)
                    {
                        var issue = NewIssue(entry, null, b, levels); issue.Kind = IssueKind.WallFloating;
                        issue.Category = "Support de mur à vérifier"; issue.IsApproximate = true;
                        issue.Message = "Aucun support proche de la base n'a été trouvé dans les murs, sols et fondations du document actif. Les supports liés et les cas particuliers ne sont pas contrôlés.";
                        issue.Fingerprint = SmartGeometry.Fingerprint(b, 0, true); AddIssue(issue, Geometry(entry));
                    }
                    yield return true;
                }
            }
            Stage = SourceCount == 0 ? "Aucun objet dans les catégories et le périmètre choisis" : "Analyse terminée";
        }
        private FilteredElementCollector ScopedCollector(Document doc) => _options.Scope == SmartScanScope.Selection
            ? new FilteredElementCollector(doc, _setup.Selection) : _options.Scope == SmartScanScope.ActiveView
            ? new FilteredElementCollector(doc, _setup.ViewId) : new FilteredElementCollector(doc);
        private Entry CreateEntry(Element element, Transform transform, Element link)
        {
            try
            {
                var box = SmartGeometry.WorldBox(element.get_BoundingBox(null), transform); if (box == null) return null;
                if (_options.IncludeInsulation)
                    foreach (var id in InsulationIds(element))
                        box = SmartGeometry.Union(new[] { box, SmartGeometry.WorldBox(element.Document.GetElement(id)?.get_BoundingBox(null), transform) });
                return new Entry { Element = element, Transform = transform, Box = box, Link = link,
                    Key = (link?.UniqueId ?? "host") + ":" + element.UniqueId };
            }
            catch (Exception ex) { Warn("box:" + element.UniqueId, "Objet écarté : " + Label(element) + " — " + ex.Message); return null; }
        }
        private GeometryData Geometry(Entry entry)
        {
            if (_geometry.TryGetValue(entry.Key, out var cached)) return cached;
            var data = new GeometryData();
            try
            {
                SmartGeometry.Collect(entry.Element.get_Geometry(_geometryOptions), entry.Transform, data.Solids, data.MeshBoxes, data.Meshes);
                if (_options.IncludeInsulation)
                    foreach (var id in InsulationIds(entry.Element))
                    {
                        var insulation = entry.Element.Document.GetElement(id);
                        if (insulation != null) SmartGeometry.Collect(insulation.get_Geometry(_geometryOptions), entry.Transform, data.Solids, data.MeshBoxes, data.Meshes);
                    }
                data.Approximate = data.MeshBoxes.Count > 0;
                if (data.Solids.Count == 0 && !data.Approximate)
                    Warn("geometry:" + entry.Key, "Pas de volume physique exploitable : " + Label(entry.Element) + " (" + (entry.Link?.Name ?? "maquette") + ").");
            }
            catch (Exception ex) { data.Approximate = true; Warn("geometry:" + entry.Key, "Géométrie partielle : " + Label(entry.Element) + " — " + ex.Message); }
            _geometry[entry.Key] = data; return data;
        }
        private SmartVisualScene Visual(GeometryData source, GeometryData target, BoundingBoxXYZ box, bool approximate)
        {
            if (source.Visual == null) source.Visual = _visuals.Read(source.Solids, source.Meshes, source.Approximate);
            if (target != null && target.Visual == null) target.Visual = _visuals.Read(target.Solids, target.Meshes, target.Approximate);
            return _visuals.Scene(source.Visual, target?.Visual, box, approximate);
        }
        private IEnumerable<ElementId> InsulationIds(Element element)
        {
            var category = (BuiltInCategory)(element.Category?.Id.GetIdLongValue() ?? 0);
            bool pipe = category == BuiltInCategory.OST_PipeCurves || category == BuiltInCategory.OST_FlexPipeCurves
                || category == BuiltInCategory.OST_PipeFitting || category == BuiltInCategory.OST_PipeAccessory;
            bool duct = category == BuiltInCategory.OST_DuctCurves || category == BuiltInCategory.OST_FlexDuctCurves
                || category == BuiltInCategory.OST_DuctFitting || category == BuiltInCategory.OST_DuctAccessory;
            var ids = new List<ElementId>();
            if (!pipe && !duct) return ids;
            try { ids.AddRange(InsulationLiningBase.GetInsulationIds(element.Document, element.Id)); }
            catch (Exception ex) { Warn("insulation:" + element.UniqueId, "Isolant non lu : " + Label(element) + " — " + ex.Message); }
            if (duct)
                try { ids.AddRange(InsulationLiningBase.GetLiningIds(element.Document, element.Id)); }
                catch (Exception ex) { Warn("lining:" + element.UniqueId, "Revêtement non lu : " + Label(element) + " — " + ex.Message); }
            return ids.Distinct();
        }
        private ModelIssue NewIssue(Entry source, Entry target, BoundingBoxXYZ box, IList<Level> levels)
        {
            var level = source.Element.Document.GetElement(source.Element.LevelId) as Level;
            if (level == null && box != null) level = levels.OrderBy(l => Math.Abs(l.Elevation - box.Min.Z)).FirstOrDefault();
            return new ModelIssue
            {
                ElementId = source.Element.Id, RelatedId = target?.Link?.Id ?? target?.Element.Id ?? ElementId.InvalidElementId,
                LinkedElementId = target?.Link is RevitLinkInstance ? target.Element.Id : ElementId.InvalidElementId,
                ElementUniqueId = source.Element.UniqueId, RelatedUniqueId = target?.Element.UniqueId, LinkUniqueId = target?.Link?.UniqueId,
                ElementCategory = source.Element.Category?.Name, ElementTypeName = TypeLabel(source.Element),
                RelatedTypeName = target == null ? null : TypeLabel(target.Element), RelatedCategory = target?.Element.Category?.Name,
                CanCreateReservation = !(source.Element is Wall) && !(source.Element is Floor)
                    && source.Element.Category?.CategoryType == CategoryType.Model && (target?.Element is Wall || target?.Element is Floor),
                LinkName = target?.Link?.Name, ElementLabel = Label(source.Element), ObstacleLabel = target == null ? null : Label(target.Element),
                LevelName = level?.Name ?? "Niveau non identifié", BBox = box,
                ScopeDescription = _options.Scope == SmartScanScope.Selection ? "Sélection" : _options.Scope == SmartScanScope.ActiveView ? "Vue active" : "Maquette entière"
            };
        }
        internal static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        private static string TypeLabel(Element e)
        {
            var type = e.Document.GetElement(e.GetTypeId());
            if (e is FamilyInstance fi) return (fi.Symbol?.Family?.Name ?? "") + " · " + (type?.Name ?? e.Name);
            return type?.Name ?? e.Name;
        }
        private static string Label(Element e) => (e.Category?.Name ?? e.GetType().Name) + " #" + e.Id.GetIdLongValue();
        private static IEnumerable<Connector> Connectors(Element e)
        {
            ConnectorSet set = null;
            try { set = (e as MEPCurve)?.ConnectorManager?.Connectors ?? (e as FamilyInstance)?.MEPModel?.ConnectorManager?.Connectors; } catch { }
            return set == null ? Enumerable.Empty<Connector>() : set.Cast<Connector>();
        }
        private static HashSet<long> ConnectedIds(Element e)
        {
            var result = new HashSet<long>();
            foreach (var c in Connectors(e))
                try { if (c.ConnectorType != ConnectorType.End) continue;
                    foreach (Connector other in c.AllRefs) if (other.Owner != null && other.ConnectorType == ConnectorType.End
                        && c.IsConnectedTo(other)) result.Add(other.Owner.Id.GetIdLongValue()); } catch { }
            return result;
        }
        private static int OpenConnectorCount(Element e)
        {
            int count = 0;
            foreach (var c in Connectors(e)) { try { if (c.ConnectorType == ConnectorType.End && !c.IsConnected) count++; } catch { } }
            return count;
        }
        private sealed class GeometryData
        {
            public List<Solid> Solids = new List<Solid>();
            public List<BoundingBoxXYZ> MeshBoxes = new List<BoundingBoxXYZ>();
            public List<(Mesh Mesh, Transform Transform)> Meshes = new List<(Mesh, Transform)>();
            public SmartVisualMesh Visual;
            public bool Approximate;
        }
        private sealed class Entry
        { public Element Element; public Element Link; public Transform Transform; public BoundingBoxXYZ Box; public string Key; }
        // Bounded sparse grid: large elements use a fallback rather than millions of cells.
        private sealed class SpatialIndex
        {
            private const double Size = 4000 / 304.8;
            private readonly Dictionary<(int, int, int), List<Entry>> _cells = new Dictionary<(int, int, int), List<Entry>>();
            private readonly List<Entry> _large = new List<Entry>();
            private readonly List<Entry> _all = new List<Entry>();
            private static (int x, int y, int z) Cell(XYZ p) => ((int)Math.Floor(p.X / Size), (int)Math.Floor(p.Y / Size), (int)Math.Floor(p.Z / Size));
            private static long Count((int x, int y, int z) a, (int x, int y, int z) b) => ((long)b.x - a.x + 1) * ((long)b.y - a.y + 1) * ((long)b.z - a.z + 1);
            public void Add(Entry e)
            {
                _all.Add(e); var a = Cell(e.Box.Min); var b = Cell(e.Box.Max);
                if (Count(a, b) > 512) { _large.Add(e); return; }
                for (int x = a.x; x <= b.x; x++) for (int y = a.y; y <= b.y; y++) for (int z = a.z; z <= b.z; z++)
                { var key = (x, y, z); if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Entry>(); list.Add(e); }
            }
            public IEnumerable<Entry> Query(BoundingBoxXYZ box)
            {
                var a = Cell(box.Min); var b = Cell(box.Max); var seen = new HashSet<string>();
                if (Count(a, b) > 512) { foreach (var e in _all) if (SmartGeometry.Overlap(box, e.Box)) yield return e; yield break; }
                foreach (var e in _large) if (SmartGeometry.Overlap(box, e.Box) && seen.Add(e.Key)) yield return e;
                for (int x = a.x; x <= b.x; x++) for (int y = a.y; y <= b.y; y++) for (int z = a.z; z <= b.z; z++)
                    if (_cells.TryGetValue((x, y, z), out var list)) foreach (var e in list)
                        if (SmartGeometry.Overlap(box, e.Box) && seen.Add(e.Key)) yield return e;
            }
        }
    }
    internal sealed class SmartIntersection
    { public BoundingBoxXYZ Box; public bool Confirmed; public bool Failed; public double Volume; }
    internal static class SmartGeometry
    {
        internal static bool Overlap(BoundingBoxXYZ a, BoundingBoxXYZ b) => a != null && b != null && OverlapXY(a, b)
            && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        internal static bool OverlapXY(BoundingBoxXYZ a, BoundingBoxXYZ b) => a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
            && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;
        internal static BoundingBoxXYZ WorldBox(BoundingBoxXYZ box, Transform outer)
        {
            if (box == null) return null;
            var tr = (outer ?? Transform.Identity).Multiply(box.Transform ?? Transform.Identity);
            var points = new List<XYZ>();
            for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                points.Add(tr.OfPoint(new XYZ(x == 0 ? box.Min.X : box.Max.X, y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z)));
            return FromPoints(points);
        }
        private static BoundingBoxXYZ FromPoints(IList<XYZ> points) => points.Count == 0 ? null : new BoundingBoxXYZ
        { Min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
            Max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)) };
        internal static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> boxes)
        { var pts = boxes.Where(b => b != null).SelectMany(b => new[] { b.Min, b.Max }).ToList(); return FromPoints(pts); }
        internal static void Collect(GeometryElement geo, Transform transform, IList<Solid> solids, IList<BoundingBoxXYZ> meshes,
            IList<(Mesh Mesh, Transform Transform)> visualMeshes = null)
        {
            if (geo == null) return;
            foreach (var item in geo)
            {
                if (item is Solid solid && solid.Faces.Size > 0 && solid.Volume > 1e-12)
                    solids.Add(transform.IsIdentity ? solid : SolidUtils.CreateTransformed(solid, transform));
                else if (item is GeometryInstance instance)
                    Collect(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), solids, meshes, visualMeshes);
                else if (item is Mesh mesh)
                { var points = mesh.Vertices.Select(transform.OfPoint).ToList(); var box = FromPoints(points); if (box != null) meshes.Add(box);
                    visualMeshes?.Add((mesh, transform)); }
            }
        }
        internal static SmartIntersection Intersection(IList<Solid> left, IList<Solid> right, BoundingBoxXYZ a, BoundingBoxXYZ b,
            double minimumVolume, bool approximate)
        {
            var result = new SmartIntersection(); if (!Overlap(a, b)) return result;
            foreach (var l in left) foreach (var r in right)
            {
                if (!Overlap(WorldBox(l.GetBoundingBox(), Transform.Identity), WorldBox(r.GetBoundingBox(), Transform.Identity))) continue;
                try
                {
                    var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(l, r, BooleanOperationsType.Intersect);
                    if (intersection != null && intersection.Volume > minimumVolume)
                    {
                        result.Confirmed = true; result.Volume = Math.Max(result.Volume, intersection.Volume);
                        result.Box = Union(new[] { result.Box, WorldBox(intersection.GetBoundingBox(), Transform.Identity) });
                    }
                }
                catch { result.Failed = true; }
            }
            if (!result.Confirmed && (approximate || result.Failed))
                result.Box = new BoundingBoxXYZ { Min = new XYZ(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z)),
                    Max = new XYZ(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z)) };
            return result;
        }
        internal static string Fingerprint(BoundingBoxXYZ b, double volume, bool approximate)
        {
            if (b == null) return "v2:none";
            return "v2:" + string.Join(",", new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }
                .Select(v => Math.Round(v * 304.8, 1).ToString("F1", CultureInfo.InvariantCulture)))
                + ":" + Math.Round(volume, 1).ToString("F1", CultureInfo.InvariantCulture) + ":" + approximate;
        }
    }
}

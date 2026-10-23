using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Analyse
{
    internal static class HistoryBackgroundWork
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LastInput { internal uint Size; internal uint Tick; }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        private static readonly uint ProcessId = (uint)Process.GetCurrentProcess().Id;
        private static bool _wasForeground;
        private static uint _foregroundSince;
        internal static bool Paused;
        internal static bool SelectionRequested;
        internal static bool CanStartSelection(bool foreground, bool requested, bool paused)
            => foreground && requested && !paused;
        internal static bool SelectionCanRun()
        {
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var foregroundProcess);
            return CanStartSelection(window != IntPtr.Zero && foregroundProcess == ProcessId, SelectionRequested, Paused);
        }

        internal static bool CanRun(uint now, uint lastInput, bool foreground, uint minimumIdleMilliseconds)
        {
            if (!foreground) { _wasForeground = false; return false; }
            if (!_wasForeground) { _wasForeground = true; _foregroundSince = now; }
            return unchecked(now - _foregroundSince) >= minimumIdleMilliseconds
                && unchecked(now - lastInput) >= minimumIdleMilliseconds;
        }

        internal static bool PreparationCanRun()
        {
            if (Paused) return false;
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var foregroundProcess);
            return window == IntPtr.Zero || foregroundProcess != ProcessId || UserIsIdle(15000);
        }
        internal static bool UserIsIdle(uint minimumIdleMilliseconds = 1000)
        {
            if (Paused) return false;
            var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var foregroundProcess);
            return GetLastInputInfo(ref input) && CanRun((uint)Environment.TickCount, input.Tick,
                window != IntPtr.Zero && foregroundProcess == ProcessId, minimumIdleMilliseconds);
        }

        // Only short inventory passes use this gate. Heavy geometry/copies keep
        // the longer foreground idle gate above. Working in another application
        // must not leave a newly opened model unprotected indefinitely.
        internal static bool CanRunInventory(uint now, uint lastInput, bool foreground, bool paused)
            => !paused && (!foreground || unchecked(now - lastInput) >= 150);

        internal static bool InventoryCanRun()
        {
            if (Paused) return false;
            var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var foregroundProcess);
            return GetLastInputInfo(ref input) && CanRunInventory((uint)Environment.TickCount, input.Tick,
                window != IntPtr.Zero && foregroundProcess == ProcessId, Paused);
        }
    }

    internal sealed class HistoryNativeArtifact
    {
        public string File { get; set; }
        public string PayloadId { get; set; }
        public string TypeVersion { get; set; }
        [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public bool DefinitionOnly { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public List<HistoryNativeContextDefinition> ContextDefinitions { get; set; }
        public string RootUniqueId { get; set; }
        public string RootSourceUniqueId { get; set; }
        public string MemberSourceUniqueId { get; set; }
        public bool Ready { get; set; }
        public string Failure { get; set; }
        public DateTime? RequestedUtc { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? ReadyUtc { get; set; }
        [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public bool WholeModel { get; set; }
        [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public int SourceElementId { get; set; }
        [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public int SourceRootElementId { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public List<int> ExcludedElementIds { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public bool? SourceSystemTypeDefined { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public string SourceSystemTypeUniqueId { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public string SourceSystemTypeName { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public List<string> SourceConnectedPeerTypeUniqueIds { get; set; }
    }

    internal sealed class HistoryNativeContextDefinition
    {
        public string SourceUniqueId { get; set; }
        public string Class { get; set; }
        public string Name { get; set; }
        public int Ordinal { get; set; }
    }

    // Generic native fallback. A wave is saved once to an immutable, separate RVT:
    // later changes to a family/type cannot rewrite an older historical snapshot.
    internal static partial class ElementHistoryNativeArchive
    {
        private sealed class Pending
        {
            internal ElementId RootId;
            internal string RootUid;
            internal string CostKey;
            internal string ElementClass;
            internal int PersistenceFailures;
            internal DateTime NotBeforeUtc;
            internal readonly List<HistoryNativeArtifact> Receipts = new List<HistoryNativeArtifact>();
        }
        private sealed class State
        {
            internal readonly Dictionary<string, Pending> Pending = new Dictionary<string, Pending>();
            internal readonly Dictionary<string, HistoryRecipe> Ready = new Dictionary<string, HistoryRecipe>();
            internal readonly HashSet<string> PriorityRoots = new HashSet<string>(StringComparer.Ordinal);
            internal DateTime PrioritySelectionUtc;
            internal double PriorityClassificationMilliseconds;
            internal DateTime? LastBackgroundCheckUtc;
            internal string LastBlockReason;
            internal DateTime? LastWaveStartedUtc;
            internal DateTime? LastWaveCompletedUtc;
            internal double LastWaveMilliseconds;
            internal DateTime NextBackgroundUtc;
            internal int PriorityViewId = -1;
            internal HashSet<string> VisibleRoots = new HashSet<string>();
            internal Document Archive;
            internal HashSet<ElementId> TemplateTypeIds;
            internal bool RetireArchive;
            internal int ArchiveWaves;
            internal int ArchiveRoots;
            internal DateTime ArchiveUsedUtc;
        }
        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>(DocumentReferenceComparer.Instance);
        private static bool _processing;
        private static State _archiveOwner;
        private static readonly HashSet<Document> OwnedArchives = new HashSet<Document>(DocumentReferenceComparer.Instance);
        private static readonly List<Document> RetiredArchives = new List<Document>();
        private static HashSet<Document> DocumentsBeforeHistoricalOpen;
        private static readonly Dictionary<int, Document> ClosingSourceDocuments = new Dictionary<int, Document>();
        private static Autodesk.Revit.ApplicationServices.Application _watchedApplication;

        // Revit can supply distinct managed wrappers for the same live document.
        // Store canonical keys by reference, and use native equality only while
        // both wrappers remain valid. Never hash or compare a closed document.
        private static bool TryGetState(Document doc, out State state)
            => TryGetState(doc, out _, out state);

        private static bool TryGetState(Document doc, out Document canonical, out State state)
        {
            canonical = null;
            state = null;
            if (ReferenceEquals(doc, null)) return false;
            if (States.TryGetValue(doc, out state))
            {
                canonical = doc;
                return true;
            }
            foreach (var pair in States)
                if (SameLiveDocument(pair.Key, doc))
                {
                    canonical = pair.Key;
                    state = pair.Value;
                    return true;
                }
            return false;
        }

        private static void SourceChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs args)
        {
            var document = args.GetDocument();
            GeneratedRailingOwners.Remove(document);
            if (TryGetState(document, out var canonical, out var state))
            {
                GeneratedRailingOwners.Remove(canonical);
                state.RetireArchive = true;
            }
        }
        private static void SourceClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs args)
        {
            if (!Owns(args.Document) && TryGetState(args.Document, out var canonical, out _))
                ClosingSourceDocuments[args.DocumentId] = canonical;
        }
        private static void SourceClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs args)
        {
            if (!ClosingSourceDocuments.TryGetValue(args.DocumentId, out var doc)) return;
            ClosingSourceDocuments.Remove(args.DocumentId);
            if (args.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded) Forget(doc);
        }
        private static void Retire(State state)
        {
            if (state.Archive != null) RetiredArchives.Add(state.Archive);
            state.Archive = null;
            state.TemplateTypeIds = null;
            state.ArchiveWaves = state.ArchiveRoots = 0;
            state.RetireArchive = false;
            if (_archiveOwner == state) _archiveOwner = null;
        }
        private static void CloseRetired(Document diagnosticSource = null)
        {
            var timer = Stopwatch.StartNew();
            foreach (var archive in RetiredArchives.ToList())
            {
                try
                {
                    if (archive.IsValidObject && !archive.Close(false)) continue;
                    ReleaseHistoricalMaterializations(archive);
                    RetiredArchives.RemoveAll(candidate => ReferenceEquals(candidate, archive));
                    OwnedArchives.Remove(archive);
                }
                catch (Exception ex) { Trace.WriteLine("History archive close: " + ex.Message); }
            }
            if (diagnosticSource != null)
                HistoryPerformanceLog.Record(diagnosticSource,"native_close",timer.Elapsed.TotalMilliseconds,0,0,false);
        }
        internal static void Shutdown()
        {
            foreach (var state in States.Values) Retire(state);
            CloseRetired();
            States.Clear();
            ClosingSourceDocuments.Clear();
            if (_watchedApplication != null)
            {
                _watchedApplication.DocumentChanged -= SourceChanged;
                _watchedApplication.DocumentClosing -= SourceClosing;
                _watchedApplication.DocumentClosed -= SourceClosed;
                _watchedApplication = null;
            }
        }

        private static void ExpediteSelectedRoots(State state)
        {
            if (!state.Pending.Keys.Any(state.PriorityRoots.Contains)) return;
            // A new selection must not inherit the long pause of an unrelated
            // archive wave. Work still starts only after the user becomes idle.
            var next = DateTime.UtcNow.AddSeconds(2);
            if (state.NextBackgroundUtc > next) state.NextBackgroundUtc = next;
        }
        private static State For(Document doc)
        {
            if (_watchedApplication == null)
            {
                _watchedApplication = doc.Application;
                _watchedApplication.DocumentChanged += SourceChanged;
                _watchedApplication.DocumentClosing += SourceClosing;
                _watchedApplication.DocumentClosed += SourceClosed;
            }
            if (!TryGetState(doc, out var state)) States[doc] = state = new State();
            return state;
        }

        internal static bool Owns(Document doc)
        {
            if (ReferenceEquals(doc, null)) return false;
            if (ContainsLiveDocument(OwnedArchives, doc)
                || (DocumentsBeforeHistoricalOpen != null && !ContainsLiveDocument(DocumentsBeforeHistoricalOpen, doc))
                || (_processing && !TryGetState(doc, out _))) return true;
            try
            {
                return doc.IsValidObject && !string.IsNullOrEmpty(doc.PathName)
                    && string.Equals(Path.GetDirectoryName(doc.PathName),
                        Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history"), StringComparison.OrdinalIgnoreCase);
            }
            catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return false; }
        }

        private static readonly ConditionalWeakTable<Document, Dictionary<ElementId, ElementId>> GeneratedRailingOwners
            = new ConditionalWeakTable<Document, Dictionary<ElementId, ElementId>>();

        private static bool IsGeneratedBaluster(Element element) => element is FamilyInstance
            && element.Category?.Id.GetIdLongValue() == -2000127; // Revit's baluster category, including generated posts.

        private static Element GeneratedRailing(Element element)
        {
            if (!IsGeneratedBaluster(element)) return null;
            var owners = GeneratedRailingOwners.GetValue(element.Document, doc =>
            {
                var result = new Dictionary<ElementId, ElementId>();
                foreach (var railing in new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>())
                    foreach (var id in railing.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance))))
                        if (IsGeneratedBaluster(doc.GetElement(id)))
                        {
                            if (result.TryGetValue(id, out var previous) && previous != railing.Id)
                                result[id] = ElementId.InvalidElementId;
                            else result[id] = railing.Id;
                        }
                return result;
            });
            if (!owners.TryGetValue(element.Id, out var owner)) return null;
            if (owner == ElementId.InvalidElementId)
                throw new InvalidOperationException("Le barreau dépend de plusieurs garde-corps ; son propriétaire est ambigu.");
            return element.Document.GetElement(owner);
        }

        private static Element Root(Element element)
        {
            var visited = new HashSet<ElementId>();
            while (visited.Add(element.Id))
            {
                Element parent = null;
                if (element.GroupId != ElementId.InvalidElementId) parent = element.Document.GetElement(element.GroupId);
                else if (IsGeneratedBaluster(element)) parent = GeneratedRailing(element);
                else if (element is StairsRun run) parent = run.GetStairs();
                else if (element is StairsLanding landing) parent = landing.GetStairs();
                else if (element is Railing railing && railing.HostId != ElementId.InvalidElementId) parent = element.Document.GetElement(railing.HostId);
                else if (element is ContinuousRail continuous) parent = element.Document.GetElement(continuous.HostRailingId);
                else if (element is FamilyInstance support && support.Host is Stairs hostStairs) parent = hostStairs;
                else if (element is FamilyInstance instance && instance.SuperComponent != null) parent = instance.SuperComponent;
                else if (element is FamilyInstance panel && panel.Host is Wall wall && wall.WallType.Kind == WallKind.Curtain) parent = wall;
                else if (element is FamilyInstance hosted && hosted.Host is HostObject hostObject) parent = hostObject;
#if REVIT2024 || REVIT2025_OR_GREATER
                else if (element is Toposolid topo && topo.HostTopoId != ElementId.InvalidElementId)
                    parent = element.Document.GetElement(topo.HostTopoId);
#endif
                if (parent == null) return element;
                element = parent;
            }
            throw new InvalidOperationException("Dépendance circulaire d’éléments natifs.");
        }

        internal static Element OpeningRoot(Element element) => Root(element);

        // Resolve a raw opening checkpoint only after an explicit restore request.
        // No background Revit document open or geometry enumeration is necessary.
        internal static HistoryRestoreRequest HydrateOpeningRequest(Document source, HistoryRestoreRequest request)
        {
            var saved = request.Recipe.Native;
            var element = source.GetElement(ElementIdExtensions.CreateElementId(saved.SourceElementId));
            if (!ElementHistoryOpeningArchive.IsEligibleElement(element) || IsCalo(element))
                throw new InvalidOperationException("L’élément n’est pas présent comme objet de modèle dans la copie d’ouverture.");
            if (!string.IsNullOrEmpty(saved.MemberSourceUniqueId) && saved.MemberSourceUniqueId != element.UniqueId)
                throw new InvalidOperationException("L’identité de l’élément ne correspond pas à la copie d’ouverture.");
            var root = Root(element);
            var excluded = saved.ExcludedElementIds == null ? null : new HashSet<int>(saved.ExcludedElementIds);
            if (excluded != null && Aggregate(source, root).Any(member => excluded.Contains(member.Id.GetIdValue())))
                throw new InvalidOperationException("L’objet ou un composant de son ensemble a changé après l’ouverture : cette ancienne version ne peut pas être restaurée comme état avant suppression.");
            var artifact = new HistoryNativeArtifact
            {
                File = saved.File, WholeModel = true, SourceElementId = saved.SourceElementId,
                SourceRootElementId = root.Id.GetIdValue(), RootUniqueId = root.UniqueId,
                RootSourceUniqueId = root.UniqueId, MemberSourceUniqueId = element.UniqueId,
                ExcludedElementIds = saved.ExcludedElementIds, Ready = true,
                RequestedUtc = saved.RequestedUtc, StartedUtc = saved.StartedUtc, ReadyUtc = saved.ReadyUtc
            };
            if (HasMepSystemType(element))
            {
                var systemType = ReadMepSystemType(element);
                artifact.SourceSystemTypeDefined = systemType != null;
                artifact.SourceSystemTypeUniqueId = systemType?.UniqueId;
                artifact.SourceSystemTypeName = systemType?.Name;
                artifact.SourceConnectedPeerTypeUniqueIds = ReadConnectedPeerSystemTypes(element);
            }
            // Use the same direct creators as a completed inventory, even when
            // deletion preceded inventory. The historical source stays read-only.
            // Complex aggregates keep their native copy and exact member mapping.
            var recipe = request.ForceNativeOpeningRestore ? null
                : ElementHistoryReconstruction.CaptureDirectForRestore(element);
            // Revit does not support CopyElements for a logical MEP system.
            // Rebuild it from its restored members; a failed system recipe must
            // not trigger another minute-long native copy attempt.
            if (recipe == null && element is MEPSystem)
                throw new InvalidOperationException("Le système MEP doit être reconstitué à partir de ses composants ; sa copie native n’est pas prise en charge.");
            if (recipe == null) recipe = Recipe(element, artifact);
            else if (!(element is MEPSystem)) recipe.OpeningFallback = artifact;
            // A live lightweight capture is more authoritative than placement
            // recalculated while opening a historical RVT in the background.
            if (request.Recipe.HistoricalPlacement != null)
                recipe.HistoricalPlacement = request.Recipe.HistoricalPlacement;
            if (recipe.Native != null && request.Recipe.Parameters != null)
                recipe.Parameters = request.Recipe.Parameters;
            if (request.Recipe.GeometryRelations?.Count > 0)
                recipe.GeometryRelations = request.Recipe.GeometryRelations
                    .Concat(recipe.GeometryRelations ?? new List<HistoryGeometryRelation>())
                    .GroupBy(ElementHistoryRelations.Key).Select(g => g.First()).ToList();
            if (request.Recipe.Connections != null)
                recipe.Connections = request.Recipe.Connections;
            recipe.NativeFallbackReason = request.Recipe.NativeFallbackReason;
            return new HistoryRestoreRequest
            {
                SourceUniqueId = element.UniqueId, Label = request.Label,
                Category = element.Category?.Name, Recipe = recipe,
                CaptureFailure = null, DeletionAudit = request.DeletionAudit,
                ForceNativeOpeningRestore = request.ForceNativeOpeningRestore,
                SuperComponentUniqueId = (element as FamilyInstance)?.SuperComponent?.UniqueId,
                FamilyTypeUniqueId = (element as FamilyInstance)?.Symbol?.UniqueId
            };
        }

        internal static bool HasMepSystemType(Element element) => element is Duct || element is FlexDuct
            || element is Pipe || element is FlexPipe;

        internal static Element ReadMepSystemType(Element element)
        {
            if (!(element is MEPCurve curve) || !HasMepSystemType(element)) return null;
            bool piping = element is Pipe || element is FlexPipe;
            bool Expected(Element candidate) => piping ? candidate is PipingSystemType : candidate is MechanicalSystemType;
            try
            {
                var parameter = element.get_Parameter(piping ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
                    : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                var assigned = parameter?.StorageType == StorageType.ElementId
                    ? element.Document.GetElement(parameter.AsElementId()) : null;
                if (Expected(assigned)) return assigned;
                var system = curve.MEPSystem;
                assigned = system == null ? null : element.Document.GetElement(system.GetTypeId());
                if (Expected(assigned)) return assigned;
            }
            catch (Exception ex) { Trace.WriteLine("History native system type: " + ex.Message); }
            var types = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var port in ElementHistoryNetwork.Ports(element))
            {
                try
                {
                    var system = port.MEPSystem;
                    var type = system == null ? null : element.Document.GetElement(system.GetTypeId());
                    if (Expected(type)) types[type.UniqueId] = type;
                }
                catch (Exception ex) { Trace.WriteLine("History native port system type: " + ex.Message); }
            }
            return types.Count == 1 ? types.Values.Single() : null;
        }

        internal static List<string> ReadConnectedPeerSystemTypes(Element element)
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in ElementHistoryNetwork.Ports(element))
            {
                try
                {
                    foreach (var peer in port.AllRefs.Cast<Connector>().Where(peer => peer.Owner.Id != element.Id
                        && !(peer.Owner is MEPSystem) && (peer.ConnectorType == ConnectorType.End
                            || peer.ConnectorType == ConnectorType.Curve || peer.ConnectorType == ConnectorType.Physical)))
                    {
                        if (!port.IsConnectedTo(peer)) continue;
                        var system = peer.MEPSystem;
                        var type = system == null ? ReadMepSystemType(peer.Owner)
                            : element.Document.GetElement(system.GetTypeId());
                        if (type is MEPSystemType) types.Add(type.UniqueId);
                    }
                }
                catch (Exception ex) { Trace.WriteLine("History native peer system type: " + ex.Message); }
            }
            return types.OrderBy(uid => uid, StringComparer.Ordinal).ToList();
        }

        internal static bool RequiresAggregate(Element element) => element.GroupId != ElementId.InvalidElementId
            || element is StairsRun || element is StairsLanding
            || (element is FamilyInstance instance && instance.Host is Wall wall && wall.WallType.Kind == WallKind.Curtain);

        private static bool IsCalo(Element element)
        {
            var category = element.Category?.Id.GetIdLongValue();
            return category == (int)BuiltInCategory.OST_PipeInsulations || category == (int)BuiltInCategory.OST_DuctInsulations
                || ElementHistoryRestoration.IsCalorifuge(element.Category?.Name,
                    element.Name + " " + (element is FamilyInstance instance ? instance.Symbol?.Family?.Name : ""));
        }

        internal static bool IsHeavyTerrain(Element element)
        {
            if (element == null) return false;
            bool Terrain(Element e) => e != null && (e.GetType().Name == "Toposolid" || e.GetType().Name == "TopographySurface");
            if (Terrain(element)) return true;
            if (element.GroupId == ElementId.InvalidElementId && !(element is Group)) return false;
            var root = Root(element);
            var pending = new Queue<Element>();
            var seen = new HashSet<ElementId>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var candidate = pending.Dequeue();
                if (candidate == null || !seen.Add(candidate.Id)) continue;
                if (Terrain(candidate)) return true;
                if (candidate is Group group)
                    foreach (var id in group.GetMemberIds()) pending.Enqueue(element.Document.GetElement(id));
            }
            return false;
        }

        internal static HistoryRecipe Capture(Element element, bool allowHeavyTerrain = true)
        {
            if (_processing || element == null || element.Document.IsFamilyDocument || element is ElementType
                || element.ViewSpecific || element.Category == null
                || IsCalo(element)
                || (!(element is Group) && element.get_BoundingBox(null) == null)) return null;
            var root = Root(element);
            if (!(root is Group) && root.Category?.CategoryType != CategoryType.Model) return Find(element.Document, element.UniqueId);
            var state = For(element.Document);
            if (state.Ready.TryGetValue(element.UniqueId, out var cached)) return cached;
            if (!allowHeavyTerrain && IsHeavyTerrain(root)) return null;
            if (!state.Pending.TryGetValue(root.UniqueId, out var pending))
                state.Pending[root.UniqueId] = pending = new Pending { RootId = root.Id, RootUid = root.UniqueId,
                    CostKey = HistoryCaptureCosts.Key(root), ElementClass = root.GetType().Name };
            var receipt = new HistoryNativeArtifact { RootSourceUniqueId = root.UniqueId,
                MemberSourceUniqueId = element.UniqueId, RequestedUtc = DateTime.UtcNow };
            var queued = pending.Receipts.FirstOrDefault(r => r.MemberSourceUniqueId == element.UniqueId);
            if (queued != null) return Recipe(element, queued);
            pending.Receipts.Add(receipt);
            return Recipe(element, receipt);
        }

        private static HistoryRecipe Recipe(Element element, HistoryNativeArtifact artifact)
        {
            var warnings = new List<string>();
            var connections = new List<HistoryConnection>();
            foreach (var member in element.UniqueId == artifact.RootSourceUniqueId ? Aggregate(element.Document,element) : new[] {element})
            {
                if (IsCalo(member)) continue;
                foreach (var connection in ElementHistoryNetwork.CaptureConnections(member,warnings))
                { connection.SourceMember = member.UniqueId; connections.Add(connection); }
            }
            var recipe = new HistoryRecipe
            {
                Kind = "native_archive", Native = artifact, Type = element.Document.GetElement(element.GetTypeId())?.UniqueId,
                RequiresMeshPreview = true, RestorationOrigins = ElementHistoryRestoration.GetOrigins(element),
                Connections = connections, CaptureWarnings = warnings,
                GeometryRelations = ElementHistoryRelations.Capture(element, warnings)
            };
            if (!(element is Group)) ElementHistoryReconstruction.CaptureHistoricalPlacement(element, recipe);
            return recipe;
        }

        internal static HistoryRecipe Find(Document doc, string uid) => TryGetState(doc, out var state)
            && state.Ready.TryGetValue(uid ?? "", out var recipe) ? recipe : null;

        internal static void GetAuditCounts(Document doc, out int pendingRoots, out int readyRecipes)
        {
            pendingRoots = 0;
            readyRecipes = 0;
            if (doc != null && TryGetState(doc, out var state))
            {
                pendingRoots = state.Pending.Count;
                readyRecipes = state.Ready.Count;
            }
        }

        internal static void Invalidate(Document doc, IEnumerable<ElementId> ids)
        {
            if (!TryGetState(doc, out var state)) return;
            var roots = new HashSet<string>();
            foreach (var id in ids)
            {
                var element = doc.GetElement(id);
                if (element == null) continue;
                if (state.Ready.TryGetValue(element.UniqueId, out var previous)) roots.Add(previous.Native.RootSourceUniqueId);
                if (!(element is ElementType))
                {
                    // Only invalidate a fallback already requested by capture. Do
                    // not enqueue supported pipes/walls/families merely because
                    // some other object initialized the archive state.
                    if (state.Pending.Count > 0)
                    {
                        var root = Root(element);
                        if (state.Pending.ContainsKey(root.UniqueId)) roots.Add(root.UniqueId);
                    }
                }
                else
                {
                    // Type edits can change the geometry of every archived instance.
                    foreach (var pair in state.Ready.Where(p => p.Value.Type == element.UniqueId)) roots.Add(pair.Value.Native.RootSourceUniqueId);
                    foreach (var pending in state.Pending.Values)
                        if (pending.Receipts.Any(r => doc.GetElement(r.MemberSourceUniqueId)?.GetTypeId() == element.Id)) roots.Add(pending.RootUid);
                }
            }
            foreach (var root in roots)
            {
                foreach (var key in state.Ready.Where(p => p.Value.Native.RootSourceUniqueId == root).Select(p => p.Key).ToList()) state.Ready.Remove(key);
                if (state.Pending.TryGetValue(root, out var pending))
                {
                    foreach (var receipt in pending.Receipts) receipt.Failure = "L’objet a changé avant la fin de son archivage natif.";
                    state.Pending.Remove(root);
                }
                var current = doc.GetElement(root);
                if (current != null) Capture(current);
            }
        }

        internal static void Forget(Document doc)
        {
            if (ReferenceEquals(doc, null)) return;
            GeneratedRailingOwners.Remove(doc);
            if (TryGetState(doc, out var canonical, out var state))
            {
                GeneratedRailingOwners.Remove(canonical);
                Retire(state);
                States.Remove(canonical);
            }
        }

        internal static void ProcessPendingBackground(Document source, bool selectionOnly = false)
        {
            if (!ElementHistoryTracker.CaptureDetailedDeletedMesh) return;
            // Native API copies can be indivisible: selection prioritizes them, but does not bypass idle.
            if (selectionOnly && !HistoryBackgroundWork.UserIsIdle()) return;
            if (HistoryBackgroundWork.UserIsIdle())
            {
                foreach (var idleState in States.Values.Where(s=>s.Archive!=null && s.Pending.Count==0
                    && DateTime.UtcNow>s.ArchiveUsedUtc.AddSeconds(30)).ToList()) Retire(idleState);
                CloseRetired();
            }
            if (source == null || !TryGetState(source, out var state) || state.Pending.Count == 0) return;
            var now = DateTime.UtcNow;
            state.LastBackgroundCheckUtc = now;
            if (!selectionOnly && now < state.NextBackgroundUtc) { state.LastBlockReason = "cooldown"; return; }
            bool CanContinue() => HistoryBackgroundWork.UserIsIdle()
                && (!selectionOnly || HistoryBackgroundWork.SelectionCanRun());
            if (!CanContinue()) { state.LastBlockReason = "user_active_or_paused"; return; }
            state.LastBlockReason = null;
            PrioritizeLastSelection(source, state);
            var view = source.ActiveView;
            if (!selectionOnly && view != null && view.Id.GetIdValue() != state.PriorityViewId
                && FilteredElementCollector.IsViewValidForElementIteration(source, view.Id))
            {
                var viewTimer = Stopwatch.StartNew();
                try
                {
                    var visibleIds = new HashSet<int>(new FilteredElementCollector(source, view.Id)
                        .WhereElementIsNotElementType().ToElementIds().Select(id => id.GetIdValue()));
                    state.VisibleRoots = new HashSet<string>(state.Pending.Values
                        .Where(p => visibleIds.Contains(p.RootId.GetIdValue())).Select(p => p.RootUid));
                    state.PriorityViewId = view.Id.GetIdValue();
                    HistoryPerformanceLog.Record(source, "native_view_priority", viewTimer.Elapsed.TotalMilliseconds,
                        visibleIds.Count, state.Pending.Count, false);
                }
                catch (Exception ex) { Trace.WriteLine("History native view priority: " + ex.Message); }
            }
            if (!CanContinue()) { state.LastBlockReason = "user_active_or_paused"; return; }
            state.LastWaveStartedUtc = DateTime.UtcNow;
            var timer = Stopwatch.StartNew();
            var pendingBefore = state.Pending.Count;
            try { ProcessPending(source, background: true, priorityOnly: selectionOnly); }
            finally
            {
                state.LastWaveCompletedUtc = DateTime.UtcNow;
                state.LastWaveMilliseconds = timer.Elapsed.TotalMilliseconds;
                // Revit document creation/copy/save requires the main API thread.
                // Give navigation/editing time between these indivisible operations.
                // A costly indivisible call must not be followed immediately by another one.
                var recoveryMs = Math.Min(5000, Math.Max(500, state.LastWaveMilliseconds));
                state.NextBackgroundUtc = DateTime.UtcNow.AddMilliseconds(recoveryMs);
                HistoryPerformanceLog.Record(source, "native_archive", timer.Elapsed.TotalMilliseconds,
                    pendingBefore - state.Pending.Count, state.Pending.Count, state.Pending.Count == 0);
            }
        }

        internal static bool ShouldRetrySelectedBackgroundSoon(Document source)
        {
            return source != null && TryGetState(source, out var state)
                && state.Pending.Values.Any(p => DateTime.UtcNow >= p.NotBeforeUtc) && HistoryBackgroundWork.UserIsIdle();
        }

        internal static bool ShouldRetryArchiveFailure(Exception failure, int failureCount)
        {
            if (failure == null || failureCount >= 3) return false;
            return failure is IOException || failure is UnauthorizedAccessException
                || failure.GetType().FullName == "Autodesk.Revit.Exceptions.FileAccessException"
                || failure.GetType().FullName == "Autodesk.Revit.Exceptions.RevitServerCommunicationException";
        }

        internal static HistoryNativeBackgroundAudit GetBackgroundAudit(Document doc)
        {
            if (doc == null || !TryGetState(doc, out var state)) return null;
            return new HistoryNativeBackgroundAudit
            {
                LastCheckUtc = state.LastBackgroundCheckUtc,
                LastBlockReason = state.LastBlockReason,
                NextEligibleUtc = state.NextBackgroundUtc == default(DateTime) ? (DateTime?)null : state.NextBackgroundUtc,
                LastWaveStartedUtc = state.LastWaveStartedUtc,
                LastWaveCompletedUtc = state.LastWaveCompletedUtc,
                LastWaveMilliseconds = state.LastWaveMilliseconds,
                PrioritySelectionUtc = state.PrioritySelectionUtc == default(DateTime)
                    ? (DateTime?)null : state.PrioritySelectionUtc
            };
        }

        private static void PrioritizeLastSelection(Document source, State state)
        {
            if (!ElementHistoryTracker.GetLatestSelectionForNativePriority(source, out var atUtc, out var selectedIds)
                || atUtc == state.PrioritySelectionUtc) return;
            var timer = Stopwatch.StartNew();
            state.PriorityRoots.Clear();
            foreach (var id in selectedIds)
            {
                try
                {
                    var element = source.GetElement(ElementIdExtensions.CreateElementId(id));
                    if (element != null) state.PriorityRoots.Add(Root(element).UniqueId);
                }
                catch (Exception ex) { Trace.WriteLine("History native priority: " + ex.Message); }
            }
            state.PrioritySelectionUtc = atUtc;
            state.PriorityClassificationMilliseconds += timer.Elapsed.TotalMilliseconds;
            ExpediteSelectedRoots(state);
        }

        internal static void PrioritizeSelectedRoots(Document source, DateTime atUtc, IEnumerable<string> rootUniqueIds)
        {
            if (source == null) return;
            var timer = Stopwatch.StartNew();
            var state = For(source);
            state.PriorityRoots.Clear();
            foreach (var uid in rootUniqueIds ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(uid)) state.PriorityRoots.Add(uid);
            state.PrioritySelectionUtc = atUtc;
            state.PriorityClassificationMilliseconds += timer.Elapsed.TotalMilliseconds;
            ExpediteSelectedRoots(state);
        }

        internal static void GetPriorityAudit(Document doc, out int pendingPriorityRoots,
            out double classificationMilliseconds)
        {
            pendingPriorityRoots = 0;
            classificationMilliseconds = 0;
            if (doc == null || !TryGetState(doc, out var state)) return;
            pendingPriorityRoots = state.Pending.Keys.Count(state.PriorityRoots.Contains);
            classificationMilliseconds = state.PriorityClassificationMilliseconds;
        }

        private sealed class DestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }
        private static CopyPasteOptions Options()
        {
            var options = new CopyPasteOptions(); options.SetDuplicateTypeNamesHandler(new DestinationTypes()); return options;
        }
        private sealed class Failures : IFailuresPreprocessor
        {
            internal string Message;
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool errors = false;
                foreach (var failure in accessor.GetFailureMessages())
                    if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure);
                    else { errors = true; Message += failure.GetDescriptionText() + " "; }
                return errors ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }

        private static HashSet<string> IncomingTypeNames(Document source, IEnumerable<Pending> wave)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<ElementId>();
            var pendingTypes = new Queue<ElementType>();
            void AddType(ElementId id)
            {
                if (id == null || id == ElementId.InvalidElementId || !seen.Add(id)) return;
                if (source.GetElement(id) is ElementType type) pendingTypes.Enqueue(type);
            }
            void AddParameterTypes(Element element)
            {
                foreach (Parameter parameter in element.Parameters)
                    if (parameter.HasValue && parameter.StorageType == StorageType.ElementId)
                        AddType(parameter.AsElementId());
            }
            foreach (var item in wave)
            {
                var root = source.GetElement(item.RootId);
                if (root == null) continue;
                foreach (var member in Aggregate(source, root))
                {
                    AddType(member.GetTypeId());
                    AddParameterTypes(member);
                }
            }
            while (pendingTypes.Count > 0)
            {
                var type = pendingTypes.Dequeue();
                names.Add(type.Name);
                AddParameterTypes(type);
            }
            return names;
        }

        internal static void ProcessPending(Document source, bool background = false, int maxRoots = 16,
            bool targetTypeRenames = true, bool reuseArchive = true, bool priorityOnly = false)
        {
            if (_processing || source == null || source.IsFamilyDocument || source.IsModifiable
                || !TryGetState(source, out var state) || state.Pending.Count == 0) return;
            _processing = true;
            Document archive = null;
            var completed = new List<Tuple<Pending, Element, Dictionary<string, Element>>>();
            var eligibleAt = DateTime.UtcNow;
            var selectedPending = state.Pending.Values.Where(p => eligibleAt >= p.NotBeforeUtc && state.PriorityRoots.Contains(p.RootUid)).ToList();
            if (priorityOnly && selectedPending.Count == 0) { _processing = false; return; }
            var wave = (selectedPending.Count > 0 ? selectedPending : state.Pending.Values.Where(p => eligibleAt >= p.NotBeforeUtc)
                .OrderByDescending(p => state.VisibleRoots.Contains(p.RootUid)).ToList())
                .OrderByDescending(p => state.VisibleRoots.Contains(p.RootUid))
                .ThenBy(p => HistoryCaptureCosts.Estimate(p.CostKey, "native_root", p.ElementClass))
                .Take(Math.Max(1, Math.Min(16, maxRoots))).ToList();
            if (wave.Count == 0) { _processing = false; return; }
            // Yield sooner during automatic work; a single Revit copy is indivisible.
            var copyBudgetMs = background ? 50 : 250;
            try
            {
                var stageTimer = Stopwatch.StartNew();
                HashSet<string> incomingNames = null;
                if (targetTypeRenames)
                {
                    try { incomingNames = IncomingTypeNames(source, wave); }
                    catch (Exception ex) { Trace.WriteLine("History native type preflight: " + ex.Message); }
                }
                HistoryPerformanceLog.Record(source, "native_type_preflight", stageTimer.Elapsed.TotalMilliseconds,
                    incomingNames?.Count ?? 0, state.Pending.Count, false);
                stageTimer.Restart();
                if (_archiveOwner != null && _archiveOwner != state) Retire(_archiveOwner);
                if (!reuseArchive || state.RetireArchive || state.ArchiveWaves>=8 || state.ArchiveRoots>=32) Retire(state);
                CloseRetired(source);
                bool created = state.Archive == null;
                if (created)
                {
                    archive = state.Archive = source.Application.NewProjectDocument(UnitSystem.Metric);
                    OwnedArchives.Add(archive);
                    state.TemplateTypeIds = new HashSet<ElementId>(new FilteredElementCollector(archive).WhereElementIsElementType().ToElementIds());
                    _archiveOwner = state;
                    HistoryPerformanceLog.Record(source, "native_create", stageTimer.Elapsed.TotalMilliseconds,
                        0, state.Pending.Count, false);
                }
                else
                {
                    archive = state.Archive;
                    HistoryPerformanceLog.Record(source, "native_reuse", stageTimer.Elapsed.TotalMilliseconds,
                        0, state.Pending.Count, false);
                }
                stageTimer.Restart();
                // A blank project's stock type names must not substitute their
                // definitions for types that were modified in the source model.
                using (var tx = new Transaction(archive, "BIMaestro - Initialiser archive native"))
                {
                    tx.Start();
                    var initializationFailures = new Failures();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(initializationFailures).SetClearAfterRollback(true));
                    foreach (var type in state.TemplateTypeIds.Select(archive.GetElement).OfType<ElementType>())
                        if (incomingNames == null || incomingNames.Contains(type.Name))
                            try { type.Name = "History-" + Guid.NewGuid().ToString("N") + "-" + type.Name; } catch { }
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit a refusé l’initialisation de l’archive : " + initializationFailures.Message);
                }
                HistoryPerformanceLog.Record(source, "native_initialize", stageTimer.Elapsed.TotalMilliseconds,
                    0, state.Pending.Count, false);
                var timer = Stopwatch.StartNew();
                foreach (var pending in wave)
                {
                    if (background && (!HistoryBackgroundWork.UserIsIdle()
                        || (priorityOnly && !HistoryBackgroundWork.SelectionCanRun()))) break;
                    var rootTimer = Stopwatch.StartNew();
                    foreach (var receipt in pending.Receipts) receipt.StartedUtc = DateTime.UtcNow;
                    var root = source.GetElement(pending.RootId);
                    if (root == null)
                    {
                        foreach (var receipt in pending.Receipts) receipt.Failure = "L’objet a été supprimé avant son archivage natif.";
                        state.Pending.Remove(pending.RootUid); continue;
                    }
                    using (var atomic = new TransactionGroup(archive,"BIMaestro - Capture native atomique"))
                    using (var tx = new Transaction(archive, "BIMaestro - Capturer objet natif"))
                    {
                        atomic.Start();
                        tx.Start();
                        var failures = new Failures();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                        try
                        {
                            using (var options = Options())
                            {
                                var nativeRoot = CopyRoot(source,root,archive,options);
                                // Give group definitions a version-specific name: using
                                // an existing destination group definition can replace
                                // its historical composition during copy/paste.
                                // Commit the ungroup operation before removing members:
                                // Revit validates group membership at transaction boundaries.
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit a refusé la copie native : " + failures.Message);
                                nativeRoot = RemoveCalo(archive, nativeRoot);
                                tx.Start();
                                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                                var mapping = MatchAggregate(source, root, archive, nativeRoot);
                                foreach (var pair in mapping)
                                {
                                    var original = source.GetElement(pair.Key);
                                    ElementHistoryRestoration.SetOrigins(pair.Value, (ElementHistoryRestoration.GetOrigins(original) ?? new List<string>()).Concat(new[] { pair.Key }));
                                }
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit a refusé la capture native : " + failures.Message);
                                if (atomic.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit a refusé l’archive native.");
                                completed.Add(Tuple.Create(pending, nativeRoot, mapping));
                            }
                        }
                        catch (Exception ex)
                        {
                            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                            if (atomic.GetStatus() == TransactionStatus.Started) atomic.RollBack();
                            foreach (var receipt in pending.Receipts) receipt.Failure = ex.Message;
                            state.Pending.Remove(pending.RootUid);
                        }
                    }
                    HistoryPerformanceLog.RecordElement(root, "native_root", rootTimer.Elapsed.TotalMilliseconds,
                        completed.Any(entry => entry.Item1 == pending));
                    // A wave shares one project creation and SaveAs. Check the
                    // budget between copies; Revit cannot interrupt a single copy.
                    if (timer.ElapsedMilliseconds >= copyBudgetMs) break;
                }
                if (completed.Count == 0) return;
                var directory = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "snapshot-" + Guid.NewGuid().ToString("N") + ".rvt");
                stageTimer.Restart();
                archive.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
                HistoryPerformanceLog.Record(source, "native_save", stageTimer.Elapsed.TotalMilliseconds,
                    completed.Count, state.Pending.Count, false);
                foreach (var entry in completed)
                {
                    foreach (var receipt in entry.Item1.Receipts)
                    {
                        receipt.File = path; receipt.RootUniqueId = entry.Item2.UniqueId; receipt.Failure = null;
                        receipt.Ready = entry.Item3.ContainsKey(receipt.MemberSourceUniqueId);
                        if (receipt.Ready) receipt.ReadyUtc = DateTime.UtcNow;
                        if (!receipt.Ready) receipt.Failure = "Le composant n’a pas été inclus dans la copie de son parent.";
                    }
                    foreach (var pair in entry.Item3)
                    {
                        var original = source.GetElement(pair.Key);
                        state.Ready[pair.Key] = Recipe(original, new HistoryNativeArtifact { File = path, RootUniqueId = entry.Item2.UniqueId,
                            RootSourceUniqueId = entry.Item1.RootUid, MemberSourceUniqueId = pair.Key,
                            Ready = true, RequestedUtc = entry.Item1.Receipts.FirstOrDefault(r => r.MemberSourceUniqueId == pair.Key)?.RequestedUtc,
                            StartedUtc = entry.Item1.Receipts.FirstOrDefault(r => r.MemberSourceUniqueId == pair.Key)?.StartedUtc,
                            ReadyUtc = DateTime.UtcNow });
                    }
                    state.Pending.Remove(entry.Item1.RootUid);
                }
                state.ArchiveWaves++;
                state.ArchiveRoots += completed.Count;
                state.ArchiveUsedUtc = DateTime.UtcNow;
                // A terrain/large mesh must not be serialized again for each later small object.
                // Keep its immutable file, but start the next wave with an empty document.
                if (new FileInfo(path).Length > 8 * 1024 * 1024)
                {
                    state.RetireArchive = true;
                    HistoryPerformanceLog.Record(source, "native_pool_size_limit", 0,
                        completed.Count, state.Pending.Count, false);
                }
            }
            catch (Exception ex)
            {
                state.RetireArchive = true;
                foreach (var pending in wave)
                {
                    pending.PersistenceFailures++;
                    bool retry = ShouldRetryArchiveFailure(ex, pending.PersistenceFailures);
                    foreach (var receipt in pending.Receipts)
                        receipt.Failure = (retry ? "Archivage natif en attente d’une nouvelle tentative : "
                            : "Archivage natif non enregistré : ") + ex.Message;
                    if (retry) pending.NotBeforeUtc = DateTime.UtcNow.AddSeconds(pending.PersistenceFailures);
                    else state.Pending.Remove(pending.RootUid);
                }
            }
            finally
            {
                if (!reuseArchive) { Retire(state); CloseRetired(source); }
                _processing = false;
            }
        }

        private static Element CopyRoot(Document source,Element root,Document destination,CopyPasteOptions options)
        {
            if(root is Group group)
            {
                // Copy the useful members directly. Copying a definition that
                // embeds DirectShapes can fail Revit's group validation before
                // an unwanted calorifuge member can even be removed.
                var members=new List<ElementId>();
                var originals=group.GetMemberIds().Select(source.GetElement).Where(e=>e!=null && !IsCalo(e)).ToList();
                var regular=originals.Where(e=>!(e is Group) && !(e is DirectShape)).ToList();
                if(regular.Count>0)
                {
                    var originalsByShape = regular.Select(e => new ShapeDescriptor(e)).ToList();
                    // A live source must remain read-only. If coincident members
                    // have no provenance yet, copy them separately so their
                    // returned identity is known, rather than trusting API order.
                    var stamped = regular.All(e => (ElementHistoryRestoration.GetOrigins(e)
                        ?? new List<string>()).Contains(e.UniqueId));
                    var ambiguous = !stamped && originalsByShape.Any(a =>
                        originalsByShape.Count(b => a.Matches(b)) > 1);
                    if (ambiguous)
                    {
                        foreach (var original in regular)
                        {
                            var member = CopyRoot(source, original, destination, options);
                            ElementHistoryRestoration.SetOrigins(member,
                                (ElementHistoryRestoration.GetOrigins(original) ?? new List<string>())
                                    .Concat(new[] { original.UniqueId }));
                            members.Add(member.Id);
                        }
                    }
                    else
                    {
                    var groupCopies=ElementTransformUtils.CopyElements(source,regular.Select(e=>e.Id).ToList(),destination,Transform.Identity,options)
                        .Select(destination.GetElement).Where(e=>e!=null && !(e is ElementType)).ToList();
                    destination.Regenerate();
                    var copyDescriptors = groupCopies.Select(e => new ShapeDescriptor(e)).ToList();
                    foreach(var original in regular)
                    {
                        var descriptor = new ShapeDescriptor(original);
                        var memberMatches=copyDescriptors.Where(candidate=>candidate.Origins.Contains(original.UniqueId)).ToList();
                        if (memberMatches.Count == 0)
                            memberMatches=copyDescriptors.Where(candidate=>descriptor.Matches(candidate)).ToList();
                        if(memberMatches.Count!=1) throw new InvalidOperationException("L’identité d’un membre du groupe est ambiguë.");
                        ElementHistoryRestoration.SetOrigins(memberMatches[0].Element,
                            (ElementHistoryRestoration.GetOrigins(original) ?? new List<string>())
                                .Concat(new[] { original.UniqueId }));
                        members.Add(memberMatches[0].Element.Id);copyDescriptors.Remove(memberMatches[0]);
                    }
                    }
                }
                foreach(var member in originals.Where(e=>e is Group || e is DirectShape))
                {
                    var copy=CopyRoot(source,member,destination,options);
                    if(copy!=null) members.Add(copy.Id);
                }
                if(members.Count==0) return null;
                var existing=destination.GetElement(destination.GetElement(members[0]).GroupId) as Group;
                var result=existing!=null && members.All(id=>destination.GetElement(id).GroupId==existing.Id)
                    ? existing : destination.Create.NewGroup(members);
                var resultId = result.Id;
                result.GroupType=(GroupType)result.GroupType.Duplicate("BIMaestro-History-"+Guid.NewGuid().ToString("N"));
                result = destination.GetElement(resultId) as Group
                    ?? throw new InvalidOperationException("Le groupe copié n’est plus disponible après le changement de type.");
                var parameters=ElementHistoryReconstruction.CaptureParameters(root);
                foreach(var parameter in parameters.Where(p=>!string.IsNullOrEmpty(p.Reference)).ToList())
                {
                    var referenced=source.GetElement(parameter.Reference);
                    Element mapped=null;
                    if(referenced is Level level)
                        mapped=new FilteredElementCollector(destination).OfClass(typeof(Level)).Cast<Level>()
                            .FirstOrDefault(l=>Math.Abs(l.ProjectElevation-level.ProjectElevation)<1e-6);
                    else if(referenced is Phase phase)
                        mapped=destination.Phases.Cast<Phase>().FirstOrDefault(p=>p.Name==phase.Name);
                    if(mapped==null) parameters.Remove(parameter);else parameter.Reference=mapped.UniqueId;
                }
                ElementHistoryReconstruction.ApplyParameters(destination,result,parameters);
                ElementHistoryRestoration.SetOrigins(result,
                    (ElementHistoryRestoration.GetOrigins(root) ?? new List<string>()).Concat(new[] { root.UniqueId }));
                destination.Regenerate();
                return destination.GetElement(resultId) as Group
                    ?? throw new InvalidOperationException("Le groupe copié n’est plus disponible après la régénération.");
            }
            var copied=ElementTransformUtils.CopyElements(source,new[]{root.Id},destination,Transform.Identity,options)
                .Select(destination.GetElement).Where(e=>e!=null && !(e is ElementType)).ToList();
            destination.Regenerate();
            var matches=copied.Where(e=>(ElementHistoryRestoration.GetOrigins(e) ?? new List<string>()).Contains(root.UniqueId)).ToList();
            if (matches.Count == 0) matches=copied.Where(e=>Same(root,e)).ToList();
            if(matches.Count==0) matches=copied.Where(e=>e.GetType()==root.GetType()
                && e.Category?.Id.GetIdLongValue()==root.Category?.Id.GetIdLongValue()).ToList();
            if(matches.Count!=1) throw new InvalidOperationException("L’identité de la copie native est ambiguë : matches=" + matches.Count
                + "; expected=" + NativeCopyIdentity(root) + "; returned=" + copied.Count + " ["
                + string.Join("; ", copied.OrderByDescending(element => element is ModelCurve).Take(16).Select(NativeCopyIdentity)) + "]");
            return matches[0];
        }

        private static string NativeCopyIdentity(Element element)
        {
            try
            {
                var result = element.UniqueId + "/" + element.GetType().Name + "/category=" + element.Category?.Id.GetIdLongValue()
                    + "/name=" + element.Name + "/ownerView=" + element.OwnerViewId.GetIdLongValue();
                if (element is ModelCurve model && model.GeometryCurve.IsBound)
                    result += "/curve=" + string.Join("->", new[] { model.GeometryCurve.GetEndPoint(0), model.GeometryCurve.GetEndPoint(1) }
                        .Select(point => string.Join(",", new[] { point.X, point.Y, point.Z }.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))));
                var bounds = element.get_BoundingBox(null);
                if (bounds != null) result += "/bbox=" + bounds.Min + "->" + bounds.Max;
                return result;
            }
            catch (Exception ex) { return "identity-unavailable:" + ex.GetType().Name + ":" + ex.Message; }
        }

        internal static IEnumerable<Element> Aggregate(Document doc, Element root)
        {
            var queue = new Queue<Element>(); queue.Enqueue(root);
            var visited = new HashSet<ElementId>();
            while (queue.Count > 0)
            {
                var element = queue.Dequeue();
                if (element == null || !visited.Add(element.Id) || element is ElementType) continue;
                yield return element;
                foreach (var id in element.GetDependentElements(null)) queue.Enqueue(doc.GetElement(id));
                if (element is Group group) foreach (var id in group.GetMemberIds()) queue.Enqueue(doc.GetElement(id));
                if (element is Stairs stairs)
                {
                    foreach (var id in stairs.GetStairsRuns()) queue.Enqueue(doc.GetElement(id));
                    foreach (var id in stairs.GetStairsLandings()) queue.Enqueue(doc.GetElement(id));
                    foreach (var id in stairs.GetStairsSupports()) queue.Enqueue(doc.GetElement(id));
                    foreach (var id in stairs.GetAssociatedRailings()) queue.Enqueue(doc.GetElement(id));
                }
                if (element is Wall wall && wall.CurtainGrid != null)
                {
                    foreach (var id in wall.CurtainGrid.GetPanelIds()) queue.Enqueue(doc.GetElement(id));
                    foreach (var id in wall.CurtainGrid.GetMullionIds()) queue.Enqueue(doc.GetElement(id));
                    foreach (var id in wall.CurtainGrid.GetUGridLineIds()) queue.Enqueue(doc.GetElement(id));
                    foreach (var id in wall.CurtainGrid.GetVGridLineIds()) queue.Enqueue(doc.GetElement(id));
                }
                if (element is Railing railing)
                {
                    queue.Enqueue(doc.GetElement(railing.TopRail));
                    foreach(var id in railing.GetHandRails()) queue.Enqueue(doc.GetElement(id));
                }
#if REVIT2024 || REVIT2025_OR_GREATER
                if (element is Toposolid topo)
                    foreach (var id in topo.GetSubDivisionIds()) queue.Enqueue(doc.GetElement(id));
#endif
            }
        }

        // Read each native bounding box once; comparing aggregate members must not make O(n²) API calls.
        private sealed class ShapeDescriptor
        {
            internal Element Element;
            internal Tuple<Type,long?> Kind;
            internal string Name;
            internal double[] Bounds;
            internal HashSet<string> Origins;
            internal ShapeDescriptor(Element element)
            {
                Element = element;
                Origins = new HashSet<string>(ElementHistoryRestoration.GetOrigins(element) ?? new List<string>(), StringComparer.Ordinal);
                Kind = Tuple.Create(element.GetType(), element.Category?.Id.GetIdLongValue());
                var box = element.get_BoundingBox(null);
                if (box == null) Name = element.Name;
                else
                {
                    var min = box.Min; var max = box.Max;
                    Bounds = new[] { min.X,min.Y,min.Z,max.X,max.Y,max.Z };
                }
            }
            internal bool Matches(ShapeDescriptor other)
            {
                if (!Kind.Equals(other.Kind)) return false;
                if (Bounds == null || other.Bounds == null) return Bounds == null && other.Bounds == null && Name == other.Name;
                for (int offset=0;offset<6;offset+=3)
                {
                    double distance=0;
                    for (int axis=0;axis<3;axis++)
                    { var delta=Bounds[offset+axis]-other.Bounds[offset+axis]; distance+=delta*delta; }
                    if (Math.Sqrt(distance)>=1e-4) return false;
                }
                return true;
            }
        }
        private static bool Same(Element a, Element b)
        {
            if (a.GetType() != b.GetType() || a.Category?.Id.GetIdLongValue() != b.Category?.Id.GetIdLongValue()) return false;
            var x = a.get_BoundingBox(null); var y = b.get_BoundingBox(null);
            if (x == null || y == null) return x == null && y == null && a.Name == b.Name;
            return x.Min.DistanceTo(y.Min) < 1e-4 && x.Max.DistanceTo(y.Max) < 1e-4;
        }
        internal static Dictionary<string, Element> MatchAggregate(Document source, Element root, Document archive, Element nativeRoot)
        {
            GeneratedRailingOwners.Remove(archive); // Copies may have been added inside the current transaction.
            var result = new Dictionary<string, Element> { [root.UniqueId] = nativeRoot };
            var candidates = Aggregate(archive, nativeRoot).Where(e => e.Id != nativeRoot.Id)
                .Select(e => new ShapeDescriptor(e)).GroupBy(descriptor => descriptor.Kind)
                .ToDictionary(group => group.Key, group => group.ToList());
            // Map generated owners first. Coincident balusters belonging to two
            // different railings must never be paired across those railings.
            foreach (var element in Aggregate(source, root).Where(e => e.Id != root.Id).OrderBy(IsGeneratedBaluster))
            {
                if (IsCalo(element)) continue;
                var original = new ShapeDescriptor(element);
                if (!candidates.TryGetValue(original.Kind, out var compatible)) continue;
                var scoped = compatible.AsEnumerable();
                if (IsGeneratedBaluster(element))
                {
                    var owner = GeneratedRailing(element);
                    Element mappedOwner = null;
                    if (owner != null) result.TryGetValue(owner.UniqueId, out mappedOwner);
                    if (owner != null && mappedOwner == null) continue;
                    if (mappedOwner != null) scoped = scoped.Where(candidate => GeneratedRailing(candidate.Element)?.Id == mappedOwner.Id);
                }
                var eligible = scoped.ToList();
                var matches = eligible.Where(candidate => candidate.Origins.Contains(element.UniqueId)).ToList();
                if (matches.Count == 0 && original.Origins.Count > 0)
                    matches = eligible.Where(candidate => candidate.Origins.Overlaps(original.Origins)).ToList();
                if (matches.Count == 0) matches = eligible.Where(candidate => original.Matches(candidate)).ToList();
                // A sole remaining object is not proof of identity: its real
                // source may be processed later, or Revit may have omitted a peer.
                if (matches.Count != 1) continue;
                result[element.UniqueId] = matches[0].Element; compatible.Remove(matches[0]);
            }
            return result;
        }

        private static Element RemoveCalo(Document doc, Element root)
        {
            var calo = Aggregate(doc, root).Where(IsCalo).Select(e => e.Id).ToList();
            if (calo.Count == 0) return root;
            if (root is Group group)
            {
                List<ElementId> members;
                using(var tx=new Transaction(doc,"BIMaestro - Dégrouper le calorifuge archivé"))
                {
                    tx.Start();members=group.UngroupMembers().ToList();
                    if(tx.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Impossible de dégrouper l’archive native.");
                }
                foreach (var member in members.Select(doc.GetElement).OfType<Group>().ToList())
                {
                    var oldId = member.Id;
                    var replacementMember = RemoveCalo(doc, member);
                    members.Remove(oldId); if(replacementMember!=null) members.Add(replacementMember.Id);
                }
                using(var tx=new Transaction(doc,"BIMaestro - Archiver le groupe sans calorifuge"))
                {
                    tx.Start();
                    foreach (var id in calo) if (doc.GetElement(id) != null) doc.Delete(id);
                    var remaining = members.Where(id => doc.GetElement(id) != null).ToList();
                    Group replacement=null;
                    if(remaining.Count>0)
                    {
                        replacement=doc.Create.NewGroup(remaining);
                        doc.GetElement(replacement.GetTypeId()).Name="BIMaestro-History-"+Guid.NewGuid().ToString("N");
                    }
                    if(tx.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Impossible d’archiver le groupe sans calorifuge.");
                    return replacement;
                }
            }
            using(var tx=new Transaction(doc,"BIMaestro - Exclure le calorifuge archivé"))
            {
                tx.Start();foreach (var id in calo) if (doc.GetElement(id) != null) doc.Delete(id);
                if(tx.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Impossible d’exclure le calorifuge de l’archive.");
            }
            return root;
        }

        internal sealed class Sources : IDisposable
        {
            private readonly Dictionary<string, Document> _documents = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<Document> _borrowed = new HashSet<Document>(DocumentReferenceComparer.Instance);
            private readonly Dictionary<string, string> _materialized = new Dictionary<string, string>(StringComparer.Ordinal);
            internal Document Open(Document destination, HistoryNativeArtifact artifact)
                => Open(destination?.Application, artifact);

            internal Document Open(Autodesk.Revit.ApplicationServices.Application application, HistoryNativeArtifact artifact)
            {
                if (application == null) throw new ArgumentNullException(nameof(application));
                if (artifact == null) throw new InvalidOperationException("L’archive native est absente.");
                string path;
                if (!string.IsNullOrEmpty(artifact.PayloadId))
                {
                    if (!_materialized.TryGetValue(artifact.PayloadId, out path))
                    {
                        path = ElementHistoryDurableStore.MaterializeNative(artifact.PayloadId);
                        if (string.IsNullOrWhiteSpace(path))
                            throw new InvalidOperationException("L’extraction de la capsule historique est absente.");
                        path = Path.GetFullPath(path);
                        _materialized.Add(artifact.PayloadId, path);
                    }
                }
                else
                {
                    var filename = Path.GetFileName(artifact.File);
                    if (filename == null || !filename.StartsWith("snapshot-",StringComparison.OrdinalIgnoreCase)
                        || !filename.EndsWith(".rvt",StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("L’archive native n’appartient pas au dossier d’historique.");
                    // New opening checkpoints are strictly local, temporary
                    // bridges. Legacy history RVTs still rebase by basename.
                    var suppliedPath = Path.IsPathRooted(artifact.File) ? Path.GetFullPath(artifact.File) : null;
                    var localBridgeRoot = Path.GetFullPath(ElementHistoryDurableBridge.LocalBridgeDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var localOpening = artifact.WholeModel && suppliedPath != null
                        && string.Equals(Path.GetDirectoryName(suppliedPath), localBridgeRoot, StringComparison.OrdinalIgnoreCase)
                        && filename.StartsWith("snapshot-opening-", StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParseExact(filename.Substring("snapshot-opening-".Length, filename.Length - "snapshot-opening-".Length - 4), "N", out _);
                    path = localOpening ? suppliedPath : Path.GetFullPath(Path.Combine(
                        CollaborativeModelTrackerStore.ActiveDirectory,"native-history",filename));
                    // The final atomic filename also proves completion for a
                    // whole-model receipt serialized before its copier finished.
                    if (!artifact.Ready && (!artifact.WholeModel || !File.Exists(path)))
                        throw new InvalidOperationException(artifact.Failure ?? "La copie d’ouverture est encore en préparation ou n’a pas pu être terminée.");
                }
                if (_documents.TryGetValue(path, out var stale) && !stale.IsValidObject)
                {
                    _documents.Remove(path);
                    _borrowed.Remove(stale);
                    OwnedArchives.Remove(stale);
                }
                if (!_documents.TryGetValue(path, out var source))
                {
                    CloseRetired();
                    var owner = States.Values.FirstOrDefault(state=>state.Archive != null && state.Archive.IsValidObject
                        && string.Equals(state.Archive.PathName,path,StringComparison.OrdinalIgnoreCase));
                    if (owner != null && owner.Archive.IsModified)
                    { Retire(owner); CloseRetired(); owner = null; }
                    if (owner != null) source = owner.Archive;
                    else
                    {
                        // Detached workshared sources have an empty PathName.
                        // Mark newly opening documents before DocumentOpened fires,
                        // while leaving the destination and other open models alone.
                        var existing = new HashSet<Document>(application.Documents.Cast<Document>(), DocumentReferenceComparer.Instance);
                        using (BeginHistoricalScope(application))
                        {
                            if (artifact.WholeModel)
                            {
                                using (var options = new OpenOptions())
                                {
                                    using (var info = BasicFileInfo.Extract(path))
                                        if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                                    source = application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options);
                                }
                            }
                            else source = application.OpenDocumentFile(path);
                            if (ContainsLiveDocument(existing, source))
                                throw new InvalidOperationException("Revit n’a pas ouvert une copie indépendante de l’archive historique.");
                            OwnedArchives.Add(source);
                        }
                    }
                    if (owner != null) _borrowed.Add(source);
                    _documents[path] = source;
                }
                return source;
            }
            public void Dispose()
            {
                // Keep the extraction leased whenever Revit refuses to close it.
                // Closing is retried by CloseRetired before its final release.
                foreach (var paths in _materialized.Values.GroupBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (_documents.TryGetValue(paths.Key, out var document))
                        DeferHistoricalMaterializations(document, paths.ToList());
                    else
                        foreach (var path in paths) ElementHistoryDurableStore.ReleaseMaterialized(path);
                }
                _materialized.Clear();
                foreach (var doc in _documents.Values.Where(doc=>!_borrowed.Contains(doc)))
                {
                    try
                    {
                        if (!doc.IsValidObject || doc.Close(false))
                        {
                            ReleaseHistoricalMaterializations(doc);
                            OwnedArchives.Remove(doc);
                        }
                        else if (!RetiredArchives.Any(candidate => ReferenceEquals(candidate, doc))) RetiredArchives.Add(doc);
                    }
                    catch (Exception ex)
                    {
                        if (!RetiredArchives.Any(candidate => ReferenceEquals(candidate, doc))) RetiredArchives.Add(doc);
                        Trace.WriteLine("History source close: " + ex.Message);
                    }
                }
                _documents.Clear(); _borrowed.Clear();
            }
        }

        internal static Element Restore(Document destination, HistoryRecipe recipe, Document source, Element existingRoot,
            out Dictionary<string, string> origins)
        {
            var artifact = recipe.Native;
            if (!string.IsNullOrEmpty(artifact.PayloadId))
                ValidateDurableTypeCompatibility(source, destination, artifact.TypeVersion);
            var root = source.GetElement(artifact.RootUniqueId);
            if (root == null) throw new InvalidOperationException("L’objet est absent de son archive native.");
            var toCopy = root;
            if (existingRoot != null)
            {
                if (artifact.MemberSourceUniqueId == artifact.RootSourceUniqueId) throw new InvalidOperationException("Le parent natif est déjà présent.");
                toCopy = Aggregate(source, root).SingleOrDefault(e =>
                    (artifact.WholeModel && e.UniqueId == artifact.MemberSourceUniqueId)
                    || (ElementHistoryRestoration.GetOrigins(e) ?? new List<string>()).Contains(artifact.MemberSourceUniqueId));
                if (toCopy == null) throw new InvalidOperationException("Le composant est absent de son archive native.");
            }
            using (var options = Options())
            {
                ICollection<ElementId> ids;
                if(toCopy is Group)
                {
                    var groupCopy=(Group)CopyRoot(source,toCopy,destination,options);
                    ids=new[]{groupCopy.Id};
                }
                else ids = ElementTransformUtils.CopyElements(source, new[] { toCopy.Id }, destination, Transform.Identity, options);
                destination.Regenerate();
                origins = new Dictionary<string, string>();
                var copied = ids.Select(destination.GetElement).Where(e => e != null && !(e is ElementType)).ToList();
                var roots = copied.Where(e => (ElementHistoryRestoration.GetOrigins(e) ?? new List<string>()).Contains(toCopy.UniqueId)).ToList();
                if (roots.Count == 0) roots = copied.Where(e => Same(toCopy,e)).ToList();
                if (roots.Count == 0) roots = copied.Where(e => e.GetType() == toCopy.GetType()
                    && e.Category?.Id.GetIdLongValue() == toCopy.Category?.Id.GetIdLongValue()).ToList();
                if (roots.Count != 1) throw new InvalidOperationException("L’identité de l’objet restauré est ambiguë.");
                if (existingRoot != null && copied.Any(e => e.Id != roots[0].Id && Same(root,e)))
                    throw new InvalidOperationException("Revit a recopié le parent existant ; cette copie a été annulée.");
                if (existingRoot is HostObject && roots[0] is FamilyInstance instance && instance.Host?.Id != existingRoot.Id)
                    throw new InvalidOperationException("Revit n’a pas rattaché la famille à son support historique existant.");
                if (artifact.WholeModel)
                {
                    // Per-object archives already omit insulation. A raw opening
                    // checkpoint must apply the same exclusion during restoration.
                    foreach (var id in Aggregate(destination, roots[0]).Concat(copied).Where(IsCalo)
                        .Select(e => e.Id).Distinct().ToList())
                        if (destination.GetElement(id) != null) destination.Delete(id);
                    destination.Regenerate();
                }
                // Revit returns only the top-level IDs for several aggregates.
                // Descendants may be regenerated from a group/stair definition,
                // so explicitly transfer their historical identities as well.
                foreach (var pair in MatchAggregate(source,toCopy,destination,roots[0]))
                    ElementHistoryRestoration.SetOrigins(pair.Value,
                        (ElementHistoryRestoration.GetOrigins(source.GetElement(pair.Key)) ?? new List<string>())
                            .Concat(artifact.WholeModel ? new[] { pair.Key } : Enumerable.Empty<string>()));
                foreach (var element in Aggregate(destination,roots[0]).Concat(copied).GroupBy(e=>e.Id).Select(g=>g.First()))
                    foreach (var origin in ElementHistoryRestoration.GetOrigins(element) ?? new List<string>()) origins[origin] = element.UniqueId;
                if (existingRoot != null && origins.ContainsKey(artifact.RootSourceUniqueId))
                    throw new InvalidOperationException("Revit a dupliqué le support existant ; cette copie a été annulée.");
                if (!origins.TryGetValue(artifact.MemberSourceUniqueId, out var uid))
                    throw new InvalidOperationException("L’identité de l’objet n’a pas été conservée lors de sa copie native.");
                return destination.GetElement(uid);
            }
        }
    }
}

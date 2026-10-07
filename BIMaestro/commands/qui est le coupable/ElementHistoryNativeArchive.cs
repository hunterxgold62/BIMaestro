using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Analyse
{
    internal static class HistoryBackgroundWork
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LastInput { internal uint Size; internal uint Tick; }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInput input);

        internal static bool UserIsIdle()
        {
            var input = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) };
            return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Tick) >= 1500;
        }
    }

    internal sealed class HistoryNativeArtifact
    {
        public string File { get; set; }
        public string RootUniqueId { get; set; }
        public string RootSourceUniqueId { get; set; }
        public string MemberSourceUniqueId { get; set; }
        public bool Ready { get; set; }
        public string Failure { get; set; }
    }

    // Generic native fallback. A wave is saved once to an immutable, separate RVT:
    // later changes to a family/type cannot rewrite an older historical snapshot.
    internal static class ElementHistoryNativeArchive
    {
        private sealed class Pending
        {
            internal ElementId RootId;
            internal string RootUid;
            internal readonly List<HistoryNativeArtifact> Receipts = new List<HistoryNativeArtifact>();
        }
        private sealed class State
        {
            internal readonly Dictionary<string, Pending> Pending = new Dictionary<string, Pending>();
            internal readonly Dictionary<string, HistoryRecipe> Ready = new Dictionary<string, HistoryRecipe>();
        }
        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>();
        private static bool _processing;
        private static DateTime _nextBackgroundUtc;
        private static State For(Document doc)
        {
            if (!States.TryGetValue(doc, out var state)) States[doc] = state = new State();
            return state;
        }

        internal static bool Owns(Document doc) => doc != null && ((_processing && !States.ContainsKey(doc))
            || (!string.IsNullOrEmpty(doc.PathName) && string.Equals(Path.GetDirectoryName(doc.PathName),
                Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history"), StringComparison.OrdinalIgnoreCase)));

        private static Element Root(Element element)
        {
            var visited = new HashSet<ElementId>();
            while (visited.Add(element.Id))
            {
                Element parent = null;
                if (element.GroupId != ElementId.InvalidElementId) parent = element.Document.GetElement(element.GroupId);
                else if (element is StairsRun run) parent = run.GetStairs();
                else if (element is StairsLanding landing) parent = landing.GetStairs();
                else if (element is Railing railing && railing.HostId != ElementId.InvalidElementId) parent = element.Document.GetElement(railing.HostId);
                else if (element is ContinuousRail continuous) parent = element.Document.GetElement(continuous.HostRailingId);
                else if (element is FamilyInstance support && support.Host is Stairs hostStairs) parent = hostStairs;
                else if (element is FamilyInstance instance && instance.SuperComponent != null) parent = instance.SuperComponent;
                else if (element is FamilyInstance panel && panel.Host is Wall wall && wall.WallType.Kind == WallKind.Curtain) parent = wall;
                else if (element is FamilyInstance hosted && hosted.Host is HostObject hostObject) parent = hostObject;
                if (parent == null) return element;
                element = parent;
            }
            throw new InvalidOperationException("Dépendance circulaire d’éléments natifs.");
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

        internal static HistoryRecipe Capture(Element element)
        {
            if (_processing || element == null || element.Document.IsFamilyDocument || element is ElementType
                || element.ViewSpecific || element.Category == null
                || IsCalo(element)
                || (!(element is Group) && element.get_BoundingBox(null) == null)) return null;
            var root = Root(element);
            if (!(root is Group) && root.Category?.CategoryType != CategoryType.Model) return Find(element.Document, element.UniqueId);
            var state = For(element.Document);
            if (state.Ready.TryGetValue(element.UniqueId, out var cached)) return cached;
            if (!state.Pending.TryGetValue(root.UniqueId, out var pending))
                state.Pending[root.UniqueId] = pending = new Pending { RootId = root.Id, RootUid = root.UniqueId };
            var receipt = new HistoryNativeArtifact { RootSourceUniqueId = root.UniqueId, MemberSourceUniqueId = element.UniqueId };
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
            return new HistoryRecipe
            {
                Kind = "native_archive", Native = artifact, Type = element.Document.GetElement(element.GetTypeId())?.UniqueId,
                RequiresMeshPreview = true, RestorationOrigins = ElementHistoryRestoration.GetOrigins(element),
                Connections = connections, CaptureWarnings = warnings
            };
        }

        internal static HistoryRecipe Find(Document doc, string uid) => States.TryGetValue(doc, out var state)
            && state.Ready.TryGetValue(uid ?? "", out var recipe) ? recipe : null;

        internal static void Invalidate(Document doc, IEnumerable<ElementId> ids)
        {
            if (!States.TryGetValue(doc, out var state)) return;
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

        internal static void Forget(Document doc) => States.Remove(doc);

        internal static void ProcessPendingBackground(Document source)
        {
            if (source == null || DateTime.UtcNow < _nextBackgroundUtc
                || !States.TryGetValue(source, out var state) || state.Pending.Count == 0) return;
            if (!HistoryBackgroundWork.UserIsIdle()) return;
            var timer = Stopwatch.StartNew();
            try { ProcessPending(source); }
            finally
            {
                // Revit document creation/copy/save requires the main API thread.
                // Give navigation/editing time between these indivisible operations.
                _nextBackgroundUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, timer.Elapsed.TotalMilliseconds * 9));
            }
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

        internal static void ProcessPending(Document source)
        {
            if (_processing || source == null || source.IsFamilyDocument || source.IsModifiable
                || !States.TryGetValue(source, out var state) || state.Pending.Count == 0) return;
            _processing = true;
            Document archive = null;
            var completed = new List<Tuple<Pending, Element, Dictionary<string, Element>>>();
            var wave = state.Pending.Values.Take(12).ToList();
            try
            {
                archive = source.Application.NewProjectDocument(UnitSystem.Metric);
                // A blank project's stock type names must not substitute their
                // definitions for types that were modified in the source model.
                using (var tx = new Transaction(archive, "BIMaestro - Initialiser archive native"))
                {
                    tx.Start();
                    foreach (var type in new FilteredElementCollector(archive).WhereElementIsElementType())
                        try { type.Name = "History-" + Guid.NewGuid().ToString("N") + "-" + type.Name; } catch { }
                    tx.Commit();
                }
                var timer = Stopwatch.StartNew();
                foreach (var pending in wave)
                {
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
                    if (timer.ElapsedMilliseconds >= 150) break;
                }
                if (completed.Count == 0) return;
                var directory = Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-history");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "snapshot-" + Guid.NewGuid().ToString("N") + ".rvt");
                archive.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 });
                foreach (var entry in completed)
                {
                    foreach (var receipt in entry.Item1.Receipts)
                    {
                        receipt.File = path; receipt.RootUniqueId = entry.Item2.UniqueId;
                        receipt.Ready = entry.Item3.ContainsKey(receipt.MemberSourceUniqueId);
                        if (!receipt.Ready) receipt.Failure = "Le composant n’a pas été inclus dans la copie de son parent.";
                    }
                    foreach (var pair in entry.Item3)
                    {
                        var original = source.GetElement(pair.Key);
                        state.Ready[pair.Key] = Recipe(original, new HistoryNativeArtifact { File = path, RootUniqueId = entry.Item2.UniqueId,
                            RootSourceUniqueId = entry.Item1.RootUid, MemberSourceUniqueId = pair.Key, Ready = true });
                    }
                    state.Pending.Remove(entry.Item1.RootUid);
                }
            }
            catch (Exception ex)
            {
                foreach (var pending in wave)
                {
                    foreach (var receipt in pending.Receipts) receipt.Failure = "Archivage natif non enregistré : " + ex.Message;
                    state.Pending.Remove(pending.RootUid);
                }
            }
            finally { try { if (archive != null) archive.Close(false); } finally { _processing = false; } }
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
                    var groupCopies=ElementTransformUtils.CopyElements(source,regular.Select(e=>e.Id).ToList(),destination,Transform.Identity,options)
                        .Select(destination.GetElement).Where(e=>e!=null && !(e is ElementType)).ToList();
                    destination.Regenerate();
                    foreach(var original in regular)
                    {
                        var memberMatches=groupCopies.Where(e=>Same(original,e)).ToList();
                        if(memberMatches.Count!=1) throw new InvalidOperationException("L’identité d’un membre du groupe est ambiguë.");
                        members.Add(memberMatches[0].Id);groupCopies.Remove(memberMatches[0]);
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
                result.GroupType=(GroupType)result.GroupType.Duplicate("BIMaestro-History-"+Guid.NewGuid().ToString("N"));
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
                destination.Regenerate();return result;
            }
            var copied=ElementTransformUtils.CopyElements(source,new[]{root.Id},destination,Transform.Identity,options)
                .Select(destination.GetElement).Where(e=>e!=null && !(e is ElementType)).ToList();
            destination.Regenerate();
            var matches=copied.Where(e=>Same(root,e)).ToList();
            if(matches.Count==0) matches=copied.Where(e=>e.GetType()==root.GetType()
                && e.Category?.Id.GetIdLongValue()==root.Category?.Id.GetIdLongValue()).ToList();
            if(matches.Count!=1) throw new InvalidOperationException("L’identité de la copie native est ambiguë.");
            return matches[0];
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
            }
        }
        private static bool Same(Element a, Element b)
        {
            if (a.GetType() != b.GetType() || a.Category?.Id.GetIdLongValue() != b.Category?.Id.GetIdLongValue()) return false;
            var x = a.get_BoundingBox(null); var y = b.get_BoundingBox(null);
            if (x == null || y == null) return x == null && y == null && a.Name == b.Name;
            return x.Min.DistanceTo(y.Min) < 1e-4 && x.Max.DistanceTo(y.Max) < 1e-4;
        }
        private static Dictionary<string, Element> MatchAggregate(Document source, Element root, Document archive, Element nativeRoot)
        {
            var result = new Dictionary<string, Element> { [root.UniqueId] = nativeRoot };
            var candidates = Aggregate(archive, nativeRoot).Where(e => e.Id != nativeRoot.Id).ToList();
            foreach (var element in Aggregate(source, root).Where(e => e.Id != root.Id))
            {
                if (IsCalo(element)) continue;
                var matches = candidates.Where(e => Same(element, e)).ToList();
                if (matches.Count == 0) matches = candidates.Where(e => e.GetType() == element.GetType()
                    && e.Category?.Id.GetIdLongValue() == element.Category?.Id.GetIdLongValue()).ToList();
                if (matches.Count != 1) continue;
                result[element.UniqueId] = matches[0]; candidates.Remove(matches[0]);
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
            internal Document Open(Document destination, HistoryNativeArtifact artifact)
            {
                if (artifact?.Ready != true) throw new InvalidOperationException(artifact?.Failure ?? "L’archive native n’était pas prête avant la suppression.");
                var filename = Path.GetFileName(artifact.File);
                if (filename == null || !filename.StartsWith("snapshot-",StringComparison.OrdinalIgnoreCase)
                    || !filename.EndsWith(".rvt",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("L’archive native n’appartient pas au dossier d’historique.");
                // Resolve under the current shared history root so mapped drive
                // letters and another workstation do not break saved receipts.
                var path = Path.GetFullPath(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-history",filename));
                if (!_documents.TryGetValue(path, out var source))
                    _documents[path] = source = destination.Application.OpenDocumentFile(path);
                return source;
            }
            public void Dispose() { foreach (var doc in _documents.Values) doc.Close(false); _documents.Clear(); }
        }

        internal static Element Restore(Document destination, HistoryRecipe recipe, Document source, Element existingRoot,
            out Dictionary<string, string> origins)
        {
            var artifact = recipe.Native;
            var root = source.GetElement(artifact.RootUniqueId);
            if (root == null) throw new InvalidOperationException("L’objet est absent de son archive native.");
            var toCopy = root;
            if (existingRoot != null)
            {
                if (artifact.MemberSourceUniqueId == artifact.RootSourceUniqueId) throw new InvalidOperationException("Le parent natif est déjà présent.");
                toCopy = Aggregate(source, root).SingleOrDefault(e => (ElementHistoryRestoration.GetOrigins(e) ?? new List<string>()).Contains(artifact.MemberSourceUniqueId));
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
                var roots = copied.Where(e => Same(toCopy,e)).ToList();
                if (roots.Count == 0) roots = copied.Where(e => e.GetType() == toCopy.GetType()
                    && e.Category?.Id.GetIdLongValue() == toCopy.Category?.Id.GetIdLongValue()).ToList();
                if (roots.Count != 1) throw new InvalidOperationException("L’identité de l’objet restauré est ambiguë.");
                if (existingRoot != null && copied.Any(e => e.Id != roots[0].Id && Same(root,e)))
                    throw new InvalidOperationException("Revit a recopié le parent existant ; cette copie a été annulée.");
                if (existingRoot is HostObject && roots[0] is FamilyInstance instance && instance.Host?.Id != existingRoot.Id)
                    throw new InvalidOperationException("Revit n’a pas rattaché la famille à son support historique existant.");
                // Revit returns only the top-level IDs for several aggregates.
                // Descendants may be regenerated from a group/stair definition,
                // so explicitly transfer their historical identities as well.
                foreach (var pair in MatchAggregate(source,toCopy,destination,roots[0]))
                    ElementHistoryRestoration.SetOrigins(pair.Value,ElementHistoryRestoration.GetOrigins(source.GetElement(pair.Key)) ?? new List<string>());
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

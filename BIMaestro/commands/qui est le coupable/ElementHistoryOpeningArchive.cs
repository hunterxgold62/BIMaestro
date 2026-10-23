using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Analyse
{
    // Keep the saved opening state without opening, copying, or saving a second
    // Revit document on the UI thread. Only filesystem work runs in the worker.
    // This fallback is deliberately limited to unchanged, saved local projects.
    internal static class ElementHistoryOpeningArchive
    {
        private sealed class State
        {
            internal readonly object Sync = new object();
            internal readonly HashSet<int> ChangedIds = new HashSet<int>();
            internal readonly HashSet<int> ChangedTypeIds = new HashSet<int>();
            internal readonly List<HistoryNativeArtifact> Receipts = new List<HistoryNativeArtifact>();
            internal string Source;
            internal string Destination;
            internal long Length;
            internal DateTime LastWriteUtc;
            internal DateTime RequestedUtc;
            internal DateTime? ReadyUtc;
            internal bool Ready;
            internal bool DefinitionChanged;
            internal bool SourceInvalidated;
            internal bool CopyVerified;
            internal List<int> FrozenChangedIds;
            internal int ChangedRevision;
            internal int FrozenRevision = -1;
            internal string Failure;
            internal Task CopyTask;
        }

        // Accessed only from Revit callbacks. Workers receive plain State data.
        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>(ElementHistoryDurableBridge.DocumentReferenceComparer.Instance);

        private static bool TryState(Document doc, out State state)
        {
            if (States.TryGetValue(doc, out state)) return true;
            if (!doc.IsValidObject) return false;
            foreach (var pair in States)
                if (pair.Key.IsValidObject && pair.Key.Equals(doc)) { state = pair.Value; return true; }
            return false;
        }

        internal static void Schedule(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument || doc.IsModified || ElementHistoryNativeArchive.Owns(doc)
                || TryState(doc, out _)) return;
            ElementHistoryDefinitionRevisions.SessionId(doc);
            try
            {
                var source = doc.PathName;
                if (string.IsNullOrWhiteSpace(source) || !Path.IsPathRooted(source)
                    || !string.Equals(Path.GetExtension(source), ".rvt", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(source)) return;
                var info = new FileInfo(source);
                var state = new State
                {
                    Source = info.FullName, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                    RequestedUtc = DateTime.UtcNow,
                    Destination = ElementHistoryDurableBridge.NewBridgePath()
                };
                if (state.Length == 0) return;
                States[doc] = state;
                state.CopyTask = Task.Run(() => CopyAsync(state));
                ElementHistoryDurableBridge.RegisterSession(doc, state.Destination, state.CopyTask);
            }
            catch (Exception ex) { Trace.WriteLine("History opening archive schedule: " + ex.Message); }
        }

        private static bool SourceMatches(State state)
        {
            var info = new FileInfo(state.Source);
            return info.Exists && info.Length == state.Length && info.LastWriteTimeUtc == state.LastWriteUtc;
        }

        private static async Task CopyAsync(State state)
        {
            var temporary = state.Destination + ".partial";
            Exception lastFailure = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (!SourceMatches(state))
                        throw new InvalidOperationException("Le fichier source a changé avant sa copie d’ouverture.");
                    Directory.CreateDirectory(Path.GetDirectoryName(state.Destination));
                    // Never prevent an ordinary Revit save or file replacement.
                    // Save callbacks and the original metadata invalidate a copy
                    // that could have raced with a write. Publication is atomic.
                    using (var input = new FileStream(state.Source, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                        1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
                    {
                        if (input.Length != state.Length || !SourceMatches(state))
                            throw new InvalidOperationException("Le fichier source a changé avant sa copie d’ouverture.");
                        await input.CopyToAsync(output, 1024 * 1024).ConfigureAwait(false);
                        await output.FlushAsync().ConfigureAwait(false);
                        if (output.Length != state.Length || !SourceMatches(state))
                            throw new InvalidOperationException("Le fichier source a changé pendant sa copie d’ouverture.");
                    }
                    if (!SourceMatches(state))
                        throw new InvalidOperationException("Le fichier source a changé avant la publication de sa copie d’ouverture.");
                    lock (state.Sync)
                    {
                        if (state.SourceInvalidated)
                            throw new InvalidOperationException("Le fichier source a été enregistré pendant sa copie d’ouverture.");
                        // These complete bytes already represent the verified
                        // opening version. A later source save cannot change them.
                        state.CopyVerified = true;
                    }
                    // Filesystem publication may be slow on a shared drive. Keep
                    // it outside Sync so a user save never waits on that IO.
                    File.Move(temporary, state.Destination);
                    lock (state.Sync)
                    {
                        state.Ready = true;
                        state.ReadyUtc = DateTime.UtcNow;
                        state.Failure = null;
                        foreach (var receipt in state.Receipts)
                        { receipt.Ready = true; receipt.ReadyUtc = state.ReadyUtc; receipt.Failure = null; }
                        state.Receipts.Clear();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    try { if (File.Exists(state.Destination)) File.Delete(state.Destination); } catch { }
                    // Publication failure must not restart a copy from a source
                    // that a later save was allowed to replace after verification.
                    if (state.CopyVerified) break;
                    // A replacement save must never silently become the opening
                    // state. Retry sharing/IO failures only while metadata agrees.
                    bool unchanged;
                    try { unchanged = SourceMatches(state); } catch { unchanged = false; }
                    if (!unchanged || !(ex is IOException || ex is UnauthorizedAccessException)) break;
                    if (attempt < 3) await Task.Delay(250 * (attempt + 1)).ConfigureAwait(false);
                }
            }
            lock (state.Sync)
            {
                state.Failure = "Copie d’ouverture indisponible : " + lastFailure?.Message;
                foreach (var receipt in state.Receipts) receipt.Failure = state.Failure;
                state.Receipts.Clear();
            }
            Trace.WriteLine(state.Failure);
        }

        internal static void Invalidate(Document doc, IEnumerable<ElementId> changedIds)
        {
            if (doc == null || !TryState(doc, out var state)) return;
            foreach (var id in changedIds ?? Enumerable.Empty<ElementId>())
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                if (state.ChangedIds.Add(id.GetIdValue())) state.ChangedRevision++;
                var element = doc.GetElement(id);
                if (element is ElementType) state.ChangedTypeIds.Add(id.GetIdValue());
                if (element is Family || element is Material) state.DefinitionChanged = true;
                // Editing an aggregate member also invalidates the saved parent.
                if (element != null && !(element is ElementType))
                {
                    try
                    {
                        if (state.ChangedIds.Add(ElementHistoryNativeArchive.OpeningRoot(element).Id.GetIdValue()))
                            state.ChangedRevision++;
                    }
                    catch { state.DefinitionChanged = true; }
                }
            }
        }

        internal static HistoryRecipe TryCreateRecipe(Document doc, Element live)
        {
            if (!IsEligibleElement(live) || doc == null || !doc.Equals(live.Document) || !TryState(doc, out var state)
                || state.DefinitionChanged || state.ChangedIds.Contains(live.Id.GetIdValue())) return null;
            try
            {
                var root = ElementHistoryNativeArchive.OpeningRoot(live);
                if (state.ChangedIds.Contains(root.Id.GetIdValue())
                    || state.ChangedTypeIds.Contains(live.GetTypeId().GetIdValue())
                    || state.ChangedTypeIds.Contains(root.GetTypeId().GetIdValue())) return null;
                var recipe = CreateRecipe(state, live.Id);
                if (recipe != null)
                {
                    if (!(live is Group))
                        ElementHistoryReconstruction.CaptureHistoricalPlacement(live, recipe, includeBoundingBox: false);
                    var warnings = new List<string>();
                    recipe.GeometryRelations = ElementHistoryRelations.CaptureExcavations(live, warnings);
                    if (warnings.Count > 0) recipe.CaptureWarnings = warnings;
                    recipe.Native.RootUniqueId = root.UniqueId;
                    recipe.Native.RootSourceUniqueId = root.UniqueId;
                    recipe.Native.MemberSourceUniqueId = live.UniqueId;
                    recipe.Native.SourceRootElementId = root.Id.GetIdValue();
                }
                return recipe;
            }
            catch (Exception ex) { Trace.WriteLine("History opening archive eligibility: " + ex.Message); return null; }
        }

        internal static bool IsEligibleElement(Element element) => element != null && !element.ViewSpecific
            && !(element is ElementType || element is Material || element is GraphicsStyle || element is View
                || element is ProjectInfo || element is Autodesk.Revit.DB.Plumbing.PipeSegment)
            && (element.Category?.CategoryType == CategoryType.Model
                // Revit classifies a model group's category as internal. It is
                // nevertheless the native owner of its physical members.
                || element is Group && element.Category?.Id.GetIdLongValue() == (long)BuiltInCategory.OST_IOSModelGroups);

        internal static HistoryRecipe TryCreateDeletedRecipe(Document doc, ElementId id)
        {
            if (doc == null || id == null || id == ElementId.InvalidElementId || !TryState(doc, out var state)
                || state.DefinitionChanged || state.ChangedTypeIds.Count > 0
                || state.ChangedIds.Contains(id.GetIdValue())) return null;
            // Its type cannot be queried after deletion. A changed type therefore
            // conservatively disables this fallback for uncaptured elements.
            return FreezeForDeletion(doc, new HistoryRecipe { Native = new HistoryNativeArtifact { WholeModel = true } }, id);
        }

        internal static HistoryRecipe FreezeForDeletion(Document doc, HistoryRecipe recipe, ElementId deletedId)
        {
            if (recipe?.Native?.WholeModel != true || doc == null || deletedId == null
                || !TryState(doc, out var state) || state.DefinitionChanged
                || state.ChangedTypeIds.Count > 0 || state.ChangedIds.Contains(deletedId.GetIdValue())
                || (recipe.Native.SourceRootElementId > 0 && state.ChangedIds.Contains(recipe.Native.SourceRootElementId))) return null;
            var frozen = CreateRecipe(state, deletedId);
            if (frozen == null) return null;
            frozen.Native.RootUniqueId = recipe.Native.RootUniqueId;
            frozen.Native.RootSourceUniqueId = recipe.Native.RootSourceUniqueId;
            frozen.Native.MemberSourceUniqueId = recipe.Native.MemberSourceUniqueId;
            frozen.Native.SourceRootElementId = recipe.Native.SourceRootElementId;
            if (state.FrozenRevision != state.ChangedRevision)
            {
                state.FrozenChangedIds = state.ChangedIds.Count == 0 ? null : state.ChangedIds.ToList();
                state.FrozenRevision = state.ChangedRevision;
            }
            frozen.Native.ExcludedElementIds = frozen.Native.SourceRootElementId > 0 ? null : state.FrozenChangedIds;
            return frozen;
        }

        // Call from DocumentSaving/SavingAs/SynchronizingWithCentral. No user save
        // waits for the copier, and a save can never replace the opening version.
        internal static void BeginSave(Document doc)
        {
            if (doc == null || !TryState(doc, out var state)) return;
            lock (state.Sync)
            {
                if (state.Ready || state.CopyVerified) return;
                state.SourceInvalidated = true;
                state.Failure = "Copie d’ouverture annulée : le fichier a été enregistré avant la fin de sa copie.";
                foreach (var receipt in state.Receipts) receipt.Failure = state.Failure;
            }
        }

        private static HistoryRecipe CreateRecipe(State state, ElementId id)
        {
            lock (state.Sync)
            {
                if (state.Failure != null) return null;
                var receipt = new HistoryNativeArtifact
                {
                    File = state.Destination, WholeModel = true, SourceElementId = id.GetIdValue(),
                    RequestedUtc = state.RequestedUtc, Ready = state.Ready, ReadyUtc = state.ReadyUtc
                };
                if (!state.Ready) state.Receipts.Add(receipt);
                return new HistoryRecipe { Kind = "native_archive", Native = receipt,
                    NativeFallbackReason = "État enregistré à l’ouverture, conservé par copie du fichier en arrière-plan." };
            }
        }

        // Test/diagnostic hook: awaiting this task never calls the Revit API.
        internal static Task GetCopyTask(Document doc) => doc != null && TryState(doc, out var state)
            ? state.CopyTask ?? Task.CompletedTask : Task.CompletedTask;

        internal static void Forget(Document doc) { if (doc != null) States.Remove(doc); }
        internal static void ForgetBySession(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            foreach (var doc in States.Where(p => string.Equals(Path.GetFileName(p.Value.Destination), "snapshot-opening-" + id + ".rvt", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
                States.Remove(doc);
        }
        internal static void Shutdown() { States.Clear(); }
    }
}

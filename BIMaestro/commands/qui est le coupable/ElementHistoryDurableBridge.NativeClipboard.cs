using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Analyse
{
    internal static partial class ElementHistoryDurableBridge
    {
        private sealed class NativePreparationPass
        {
            internal List<Prepared> Pending;
            internal readonly List<Prepared> Classified = new List<Prepared>();
            internal readonly List<Prepared> Physical = new List<Prepared>();
            internal readonly List<Prepared> Autonomous = new List<Prepared>();
            internal readonly List<Prepared> Selected = new List<Prepared>();
            internal List<Prepared> Publishing;
            internal HistoryRestoreRequest FirstRequest;
            internal Document Source;
            internal long Generation;
            internal int Phase, Cursor, RecoveryAttempts;
            internal readonly HashSet<string> RecoveredFailureIdentities = new HashSet<string>(StringComparer.Ordinal);
        }
        private static NativePreparationPass NativePass;

        [StructLayout(LayoutKind.Sequential)]
        private sealed class NativeMemoryStatus
        {
            internal uint Length = (uint)Marshal.SizeOf(typeof(NativeMemoryStatus));
            internal uint Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
            internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] NativeMemoryStatus status);
        // Keep ordinary networks together so template/type preparation and SaveAs
        // are paid once. This bounds roots, never traverses the remaining model.
        internal static int NativeNetworkRootLimit(ulong availablePhysical, uint memoryLoad)
        {
            if (memoryLoad >= 85) return 256;
            const ulong gib = 1024UL * 1024 * 1024;
            if (availablePhysical >= 4 * gib) return 1024;
            if (availablePhysical >= 2 * gib) return 512;
            return 256;
        }
        private static int NativeNetworkRootLimit()
        {
            try
            {
                var memory = new NativeMemoryStatus();
                if (GlobalMemoryStatusEx(memory)) return NativeNetworkRootLimit(memory.AvailablePhysical, memory.Load);
            }
            catch (Exception ex) { Trace.WriteLine("History batch memory estimate: " + ex.Message); }
            return 256;
        }

        private static void ResetNativePreparation()
        {
            NativePass = null;
            try { Sources?.Dispose(); }
            finally { Sources = null; OpenBridgeFile = null; PreparedJobs.Clear(); }
        }

        // Each call returns to Revit between opening, bounded metadata reads, export and IO.
        private static void PrepareNativeClipboard(Application application, List<ElementHistoryEvent> jobs, ElementHistoryEvent first)
        {
            var pass = NativePass;
            IDisposable reservation = null;
            string scratch = null;
            var publishing = new List<Prepared>();
            try
            {
                if (pass != null && pass.Generation != PreparationGeneration)
                {
                    ResetNativePreparation();
                    return;
                }
                if (pass == null)
                {
                    var firstRequest = TryGetOpeningNativeRequest(first);
                    if (firstRequest == null) return;
                    pass = new NativePreparationPass { FirstRequest = firstRequest, Generation=PreparationGeneration,
                        Pending = jobs.Where(e => !Errors.ContainsKey(EventId(e)) && e.ModelKey == first.ModelKey
                            && e.DeletionAudit?.BatchId == first.DeletionAudit?.BatchId)
                            .Select(e => new Prepared { Event=e, Request=TryGetOpeningNativeRequest(e) })
                            .Where(p => p.Request != null && string.Equals(p.Request.Recipe.Native.File,
                                firstRequest.Recipe.Native.File,StringComparison.OrdinalIgnoreCase)).ToList() };
                    NativePass = pass;
                    SetPreparationProgress("Ouverture de la copie historique…",0,pass.Pending.Count,pass.Generation);
                    return;
                }
                if (pass.Phase == 0)
                {
                    if (Sources != null && !string.Equals(OpenBridgeFile,pass.FirstRequest.Recipe.Native.File,StringComparison.OrdinalIgnoreCase))
                        ResetPreparationSource();
                    Sources ??= new ElementHistoryNativeArchive.Sources();
                    OpenBridgeFile = pass.FirstRequest.Recipe.Native.File;
                    pass.Source = Sources.Open(application,pass.FirstRequest.Recipe.Native);
                    pass.Phase = 1;
                    SetPreparationProgress("Lecture des éléments supprimés…",0,pass.Pending.Count,pass.Generation);
                    return;
                }
                if (pass.Phase == 1)
                {
                    var timer=Stopwatch.StartNew(); int count=0;
                    while (pass.Cursor < pass.Pending.Count && count < 96 && (count == 0 || timer.ElapsedMilliseconds < 20))
                    {
                        var item=pass.Pending[pass.Cursor++]; count++;
                        try
                        {
                            item.Request.ClassificationOnly=true;
                            PrepareCompactOpeningRequest(pass.Source,item.Request);
                            item.Request.ClassificationOnly=false; item.Request.NativeGeneration=true;
                            item.Request.SourceUniqueId=item.Request.OpeningClassification.SourceUniqueId;
                            item.Request.Category=item.Request.OpeningClassification.Category;
                            if (item.Request.OpeningClassification.IsExcluded())
                            { item.Request.Recipe=null; item.Request.CaptureFailure="Hors périmètre : dépendance non physique identifiée dans la copie native."; }
                            else
                            {
                                var autonomous = CaptureAutonomousOpeningRequest(pass.Source, item);
                                if (autonomous != null) pass.Autonomous.Add(new Prepared { Event = item.Event, Request = autonomous });
                                ElementHistoryNativeArchive.ValidateDurableNativeMember(pass.Source, item.Request);
                                pass.Physical.Add(item);
                            }
                            pass.Classified.Add(item);
                        }
                        catch (Exception ex) { RecordFailure(EventId(item.Event),ex); }
                    }
                    SetPreparationProgress("Lecture des éléments supprimés…",pass.Cursor,pass.Pending.Count,pass.Generation);
                    if (pass.Cursor == pass.Pending.Count) pass.Phase=2;
                    return;
                }
                if (pass.Autonomous.Count > 0)
                {
                    var autonomousBatch = pass.Autonomous.ToList();
                    pass.Autonomous.Clear();
                    var autonomousGeneration = pass.Generation;
                    SetPreparationProgress("Enregistrement des recettes JSON…", 0, autonomousBatch.Count, autonomousGeneration);
                    Io = Task.Run(() =>
                    {
                        int completed = 0;
                        foreach (var item in autonomousBatch)
                        {
                            try { PublishAutonomousPrepared(item); }
                            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("History autonomous publication: " + EventId(item.Event) + " : " + ex); }
                            completed++;
                            if (completed % 8 == 0 || completed == autonomousBatch.Count)
                                SetPreparationProgress("Enregistrement des recettes JSON…", completed, autonomousBatch.Count, autonomousGeneration);
                        }
                        SignalReadiness();
                    });
                    return;
                }
                // Publication or an excluded-object ACK can drain jobs between API callbacks.
                // Keep only unprocessed requests; selected requests are never reused after export.
                lock(Sync)
                {
                    Predicate<Prepared> stale=p=>!Pending.ContainsKey(EventId(p.Event)) || Errors.ContainsKey(EventId(p.Event));
                    pass.Pending.RemoveAll(stale); pass.Classified.RemoveAll(stale); pass.Physical.RemoveAll(stale);
                    pass.Selected.RemoveAll(stale); pass.Publishing?.RemoveAll(stale);
                }
                if (pass.Phase == 2)
                {
                    var networks=pass.Physical.Where(p => {
                        var e=pass.Source.GetElement(p.Request.SourceUniqueId);
                        return e is MEPCurve || e is FamilyInstance family && family.MEPModel!=null;
                    }).ToList();
                    var candidates=networks.Count>0 ? networks : pass.Physical;
                    var roots=new HashSet<string>(StringComparer.Ordinal);
                    pass.Selected.Clear();
                    int rootLimit=networks.Count>0 ? NativeNetworkRootLimit() : 32;
                    // Unattended waves keep each export short so the user regains Revit quickly.
                    if (AutomaticPass) rootLimit=Math.Min(rootLimit,32);
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            var root=ElementHistoryNativeArchive.OpeningRoot(pass.Source.GetElement(candidate.Request.SourceUniqueId)).UniqueId;
                            if (!roots.Contains(root) && roots.Count>=rootLimit) continue;
                            roots.Add(root); pass.Selected.Add(candidate);
                        }
                        catch(Exception ex) { RecordFailure(EventId(candidate.Event),ex); }
                    }
                    pass.Publishing=pass.Classified.Where(p=>p.Request.OpeningClassification.IsExcluded()).Take(256).Concat(pass.Selected).ToList();
                    if (pass.Publishing.Count==0) { NativePass=null; return; }
                    pass.Phase=3;
                    SetPreparationProgress("Copie du lot demandé…",0,pass.Selected.Count,pass.Generation);
                    return;
                }
                publishing=pass.Publishing ?? new List<Prepared>();
                if (publishing.Count==0) { pass.Phase=2; return; }
                var selected=pass.Selected;
                if (selected.Count>0)
                {
                    scratch=NewBridgePath();
                    reservation=ElementHistoryBridgeCache.BeginCopy(LocalBridgeDirectory,scratch,new FileInfo(pass.FirstRequest.Recipe.Native.File).Length);
                    var recipes=ElementHistoryNativeArchive.ExportDurableNativeBatch(pass.Source,selected.Select(p=>p.Request),scratch);
                    foreach(var item in selected)
                    {
                        if (!recipes.TryGetValue(item.Request.SourceUniqueId,out var recipe))
                            throw new InvalidDataException("Le copier-coller natif n’a pas conservé tous les objets du lot.");
                        item.Request.Recipe=recipe; item.Request.CaptureFailure=null;
                    }
                }
                SetPreparationProgress("Enregistrement des données récupérées…",0,publishing.Count,pass.Generation);
                var ownedReservation=reservation;var ownedScratch=scratch;var batch=publishing.ToList();
                var generation=pass.Generation;
                var exportingIds=new HashSet<string>(batch.Select(item=>EventId(item.Event)),StringComparer.Ordinal);
                pass.Pending.RemoveAll(item=>exportingIds.Contains(EventId(item.Event)));
                pass.Classified.RemoveAll(item=>exportingIds.Contains(EventId(item.Event)));
                pass.Physical.RemoveAll(item=>exportingIds.Contains(EventId(item.Event)));
                pass.Selected.Clear();pass.Publishing=null;pass.Phase=2;
                // The original opening file is immutable. Reuse its source and classifications
                // for the remaining waves rather than reading every remaining object again.
                NativePass=pass.Classified.Count>0 ? pass : null;
                reservation=null;scratch=null;
                Io=Task.Run(() =>
                {
                    var published=new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        var payload=ownedScratch==null ? null : ElementHistoryDurableStore.PublishNative(ownedScratch);
                        foreach(var item in batch)
                        {
                            if(item.Request.Recipe!=null)BindPayload(item.Request.Recipe,payload);
                            Publish(item);published.Add(EventId(item.Event));
                            if(published.Count%8==0 || published.Count==batch.Count)
                                SetPreparationProgress("Enregistrement des données récupérées…",published.Count,batch.Count,generation);
                        }
                    }
                    catch(Exception ex) {foreach(var item in batch.Where(p=>!published.Contains(EventId(p.Event))))RecordFailure(EventId(item.Event),ex);}
                    finally
                    {
                        ownedReservation?.Dispose();
                        if(ownedScratch!=null)try{ElementHistoryBridgeCache.Delete(LocalBridgeDirectory,ownedScratch);}catch{}
                        SignalReadiness();
                    }
                });
            }
            catch(Exception ex)
            {
                if (TryRecoverNativeMemberFailure(pass, ex)) return;
                var failed=publishing.Count>0 ? publishing : pass?.Pending ?? new List<Prepared>();
                foreach(var item in failed)
                {
                    bool pending;
                    lock(Sync) pending=Pending.ContainsKey(EventId(item.Event));
                    if(pending)RecordFailure(EventId(item.Event),ex);
                }
                ResetNativePreparation();
            }
            finally
            {
                reservation?.Dispose();
                if(scratch!=null)try{ElementHistoryBridgeCache.Delete(LocalBridgeDirectory,scratch);}catch{}
            }
        }
        // Keep native ownership indivisible, but let independent requested objects
        // be prepared even when a failed neighbour shares their MEP system or ports.
        // Recorded connection metadata is retained for subsequent restoration.
        private static bool TryRecoverNativeMemberFailure(NativePreparationPass pass, Exception error)
        {
            if (pass == null || pass.RecoveryAttempts >= 2 || pass.Source?.IsValidObject != true) return false;
            if (!TryCollectNativeMemberFailures(error, out var failures)) return false;
            var identities = failures.Select(NativeFailureIdentity).ToList();
            if (identities.Any(pass.RecoveredFailureIdentities.Contains)) return false;
            if (!TryFindFailedNativeComponents(pass, failures, out var quarantined)) return false;
            foreach (var identity in identities) pass.RecoveredFailureIdentities.Add(identity);
            pass.RecoveryAttempts++;
            foreach (var item in quarantined)
            {
                bool pending;
                lock (Sync) pending = Pending.ContainsKey(EventId(item.Event));
                if (pending) RecordFailure(EventId(item.Event), error);
            }
            var rejected = new HashSet<Prepared>(quarantined);
            pass.Pending.RemoveAll(rejected.Contains); pass.Classified.RemoveAll(rejected.Contains); pass.Physical.RemoveAll(rejected.Contains);
            pass.Selected.Clear(); pass.Publishing = null; pass.Phase = 2;
            if (pass.Classified.Count == 0) ResetNativePreparation();
            else
            {
                NativePass = pass;
                SetPreparationProgress("Poursuite des ensembles indépendants…", 0, pass.Physical.Count, pass.Generation);
            }
            return true;
        }

        private static string NativeFailureIdentity(ElementHistoryNativeArchive.NativePreparationMemberException failure)
            => failure.Stage + "|" + failure.SourceUniqueId + "|" + failure.RootSourceUniqueId
                + "|" + (failure.InnerException?.GetType().FullName ?? failure.GetType().FullName)
                + ":" + (failure.InnerException?.Message ?? failure.Message);

        private static bool TryCollectNativeMemberFailures(Exception error,
            out List<ElementHistoryNativeArchive.NativePreparationMemberException> failures)
        {
            failures = new List<ElementHistoryNativeArchive.NativePreparationMemberException>();
            if (error == null) return false;
            var pending = new Stack<Exception>(); pending.Push(error);
            var visited = new HashSet<Exception>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current)) continue;
                if (visited.Count > 10000) return false;
                if (current is ElementHistoryNativeArchive.NativePreparationMemberException known)
                {
                    if (!(string.Equals(known.Stage, "verify_native_placements", StringComparison.Ordinal)
                        || string.Equals(known.Stage, "copy_native_set", StringComparison.Ordinal))
                        || string.IsNullOrWhiteSpace(known.SourceUniqueId) || string.IsNullOrWhiteSpace(known.RootSourceUniqueId)) return false;
                    if (identities.Add(NativeFailureIdentity(known))) failures.Add(known);
                }
                else if (current is AggregateException aggregate)
                {
                    if (aggregate.InnerExceptions.Count == 0) return false;
                    foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
                }
                else if (current.InnerException != null) pending.Push(current.InnerException);
                else return false; // Every failure leaf must be explicitly localized.
            }
            return failures.Count > 0;
        }

        private static bool TryFindFailedNativeComponents(NativePreparationPass pass,
            List<ElementHistoryNativeArchive.NativePreparationMemberException> failures, out List<Prepared> quarantined)
        {
            quarantined = null;
            try
            {
                var timer = Stopwatch.StartNew();
                var roots = new Dictionary<string, Element>(StringComparer.Ordinal);
                var parents = new Dictionary<string, string>(StringComparer.Ordinal);
                var requests = new Dictionary<Prepared, string>();
                var members = new Dictionary<string, Element>(StringComparer.Ordinal);
                var memberRoots = new Dictionary<string, string>(StringComparer.Ordinal);
                string Find(string key)
                {
                    var root = key;
                    while (parents[root] != root) root = parents[root];
                    while (parents[key] != key) { var next = parents[key]; parents[key] = root; key = next; }
                    return root;
                }
                void Join(string a, string b) { a = Find(a); b = Find(b); if (a != b) parents[b] = a; }
                // Bound recovery work. If any dependency cannot be proved within
                // this budget, retain the original atomic failure instead.
                foreach (var item in pass.Physical)
                {
                    if (timer.ElapsedMilliseconds > 500 || requests.Count >= 10000) return false;
                    var element = pass.Source.GetElement(item.Request.SourceUniqueId);
                    if (element?.IsValidObject != true) return false;
                    var root = ElementHistoryNativeArchive.OpeningRoot(element);
                    requests.Add(item, root.UniqueId);
                    if (!roots.ContainsKey(root.UniqueId)) { roots.Add(root.UniqueId, root); parents.Add(root.UniqueId, root.UniqueId); }
                }
                var failedRoots = new HashSet<string>(StringComparer.Ordinal);
                foreach (var failure in failures)
                {
                    if (timer.ElapsedMilliseconds > 500) return false;
                    var failedMember = pass.Source.GetElement(failure.SourceUniqueId);
                    if (failedMember?.IsValidObject != true
                        || ElementHistoryNativeArchive.OpeningRoot(failedMember).UniqueId != failure.RootSourceUniqueId
                        || !roots.ContainsKey(failure.RootSourceUniqueId)) return false;
                    failedRoots.Add(failure.RootSourceUniqueId);
                }
                foreach (var root in roots)
                foreach (var member in ElementHistoryNativeArchive.Aggregate(pass.Source, root.Value))
                {
                    if (timer.ElapsedMilliseconds > 500 || members.Count >= 10000) return false;
                    if (!ElementHistoryNativeArchive.IsDocumentModelElement(pass.Source, member)) continue;
                    // Match the physical provenance filter used by CopyNativeSet.
                    if (new HistoryOpeningClassification { SourceElementId = member.Id.GetIdValue(), SourceUniqueId = member.UniqueId,
                        Class = member.GetType().FullName, CategoryId = member.Category?.Id.GetIdLongValue() ?? 0,
                        ViewSpecific = member.ViewSpecific }.IsExcluded()) continue;
                    if (memberRoots.TryGetValue(member.UniqueId, out var previous)) Join(root.Key, previous);
                    else { memberRoots.Add(member.UniqueId, root.Key); members.Add(member.UniqueId, member); }
                }
                int relations = 0;
                foreach (var member in members)
                {
                    if (timer.ElapsedMilliseconds > 500) return false;
                    var root = memberRoots[member.Key];
                    if (member.Value is FamilyInstance family)
                    {
                        foreach (var parent in new[] { family.Host, family.SuperComponent })
                        {
                            if (++relations > 20000) return false;
                            if (parent != null && memberRoots.TryGetValue(parent.UniqueId, out var parentRoot)) Join(root, parentRoot);
                        }
                    }
                }
                var failedComponents = new HashSet<string>(failedRoots.Select(Find), StringComparer.Ordinal);
                quarantined = requests.Where(pair => failedComponents.Contains(Find(pair.Value))).Select(pair => pair.Key).ToList();
                return quarantined.Count > 0;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("History native component could not be isolated: " + ex.Message);
                return false;
            }
        }

        private static void ResetPreparationSource()
        {
            try { Sources?.Dispose(); }
            finally { Sources=null;OpenBridgeFile=null; }
        }
    }
}

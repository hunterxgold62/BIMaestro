using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BIMaestro.ViewHover
{
    // Session-only potential impact: previous/current view-scoped collector membership.
    // No exact pixel visibility, linked-model history, or parameter/type dependency graph.
    // All Revit access is on DocumentChanged / ViewActivated / Idling, NEVER hover.
    internal static class ViewDeckChangeService
    {
        private const int MaxMembers = 50000, MaxEvents = 5000, MaxSnapshots = 100000;
        private sealed class Snapshot
        {
            internal string Category;
            internal double[] Location;
        }
        private sealed class DocumentState
        {
            internal long Sequence;
            internal readonly Dictionary<string, ViewDeckChangeWindow> Views = new Dictionary<string, ViewDeckChangeWindow>();
            internal readonly Dictionary<long, Snapshot> Snapshots = new Dictionary<long, Snapshot>();
            internal readonly Queue<long> Prime = new Queue<long>();
            internal readonly HashSet<long> Queued = new HashSet<long>();
            internal readonly List<ViewDeckChange> Events = new List<ViewDeckChange>();
        }
        private static readonly Dictionary<Document, DocumentState> Documents = new Dictionary<Document, DocumentState>();
        private static Document _activeDocument;
        private static string _activeView;

        private static DocumentState State(Document document)
        {
            if (!Documents.TryGetValue(document, out DocumentState state))
                Documents.Add(document, state = new DocumentState());
            return state;
        }
        private static ViewDeckChangeWindow State(DocumentState document, string viewId)
        {
            if (!document.Views.TryGetValue(viewId, out ViewDeckChangeWindow state))
                document.Views.Add(viewId, state = new ViewDeckChangeWindow { Baseline = document.Sequence });
            return state;
        }

        internal static void Activate(Document document, View view)
        {
            if (document == null || view == null) return;
            _activeDocument = document;
            _activeView = view.UniqueId;
            DocumentState state = State(document);
            State(state, view.UniqueId).Visit(state.Sequence);
        }

        internal static void Track(Document document, DocumentChangedEventArgs args)
        {
            if (document == null || !document.IsValidObject) return;
            DocumentState state = State(document);
            long sequence = ++state.Sequence;
            bool modelChanged = false;
            try
            {
                var added = args.GetAddedElementIds();
                var modified = args.GetModifiedElementIds();
                var deleted = args.GetDeletedElementIds();
                int processed = 0;
                foreach (var batch in new[] {
                    new { Ids = deleted, Kind = ViewDeckChangeKind.Deleted },
                    new { Ids = added, Kind = ViewDeckChangeKind.Added },
                    new { Ids = modified, Kind = ViewDeckChangeKind.Modified } })
                {
                    foreach (ElementId id in batch.Ids)
                    {
                        if (++processed > MaxEvents) { MarkPartial(state); break; }
                        long key = id.GetIdLongValue();
                        state.Snapshots.TryGetValue(key, out Snapshot previous);
                        Snapshot current = null;
                        if (batch.Kind != ViewDeckChangeKind.Deleted)
                        {
                            Element element = document.GetElement(id);
                            if (element is View) continue; // Navigation/camera changes are not model edits.
                            if (element?.Category?.Id.GetIdLongValue() == (long)BuiltInCategory.OST_Cameras) continue;
                            if (element is ElementType)
                            { MarkPartial(state); continue; } // Indirect graphic effects are outside V1.
                            current = Capture(element);
                        }
                        var kind = batch.Kind;
                        if (kind == ViewDeckChangeKind.Modified && ViewDeckChange.IsTranslation(previous?.Location, current?.Location))
                            kind = ViewDeckChangeKind.Moved;
                        state.Events.Add(new ViewDeckChange { Id = key, Sequence = sequence, Kind = kind,
                            Category = current?.Category ?? previous?.Category ?? "Éléments (catégorie inconnue)" });
                        modelChanged = true;
                        if (current != null && (state.Snapshots.ContainsKey(key) || state.Snapshots.Count < MaxSnapshots))
                            state.Snapshots[key] = current;
                        else if (kind == ViewDeckChangeKind.Deleted) state.Snapshots.Remove(key);
                    }
                }
                if (state.Events.Count > MaxEvents)
                {
                    state.Events.RemoveRange(0, state.Events.Count - MaxEvents);
                    MarkPartial(state);
                }
            }
            catch (Exception ex) { MarkPartial(state); Trace.WriteLine("ViewDeck change capture: " + ex); }
            finally
            {
                if (!modelChanged) state.Sequence--; // No collector refresh for mere camera/navigation events.
                if (Equals(_activeDocument, document) && _activeView != null && state.Views.TryGetValue(_activeView, out ViewDeckChangeWindow visible))
                {
                    // Changes made while looking at this view have already been seen,
                    // even if its next deferred membership scan has not run yet.
                    visible.Acknowledge(state.Sequence);
                }
            }
        }

        private static void MarkPartial(DocumentState state)
        {
            foreach (ViewDeckChangeWindow view in state.Views.Values) view.Journal.Partial = true;
        }

        internal static void CaptureSelection(Document document, IEnumerable<ElementId> selected)
        {
            // Prioritise the door/wall the user is about to edit/delete instead of
            // waiting for the whole view's incremental snapshot queue to finish.
            if (document == null || selected == null) return;
            DocumentState state = State(document);
            var budget = Stopwatch.StartNew();
            foreach (ElementId id in selected.Take(25))
            {
                if (budget.ElapsedMilliseconds >= 10) break;
                try
                {
                    long key = id.GetIdLongValue();
                    Snapshot snapshot = Capture(document.GetElement(id));
                    if (snapshot != null && (state.Snapshots.ContainsKey(key) || state.Snapshots.Count < MaxSnapshots))
                        state.Snapshots[key] = snapshot;
                }
                catch (Exception ex) { Trace.WriteLine("ViewDeck selected snapshot: " + ex.Message); }
            }
        }

        internal static void Process(Document document, IEnumerable<View> openViews, View activeView)
        {
            try { ProcessCore(document, openViews, activeView); }
            catch (Exception ex)
            {
                if (document != null && Documents.TryGetValue(document, out DocumentState state))
                    foreach (ViewDeckChangeWindow view in state.Views.Values) { view.Journal.Partial = true; view.Failed = true; }
                Trace.WriteLine("ViewDeck change analysis: " + ex); // Never disable previews because of tracking.
            }
        }

        private static void ProcessCore(Document document, IEnumerable<View> openViews, View activeView)
        {
            foreach (Document closed in Documents.Keys.Where(d => !d.IsValidObject).ToList()) Documents.Remove(closed);
            if (document == null || !document.IsValidObject) return;
            if (!Equals(_activeDocument, document) || _activeView != activeView?.UniqueId) Activate(document, activeView);
            DocumentState state = State(document);
            var views = openViews.ToDictionary(v => v.UniqueId);
            foreach (string closed in state.Views.Keys.Where(id => !views.ContainsKey(id)).ToList()) state.Views.Remove(closed);
            foreach (string id in views.Keys) State(state, id);
            // One view's membership per refresh, only in the active document.
            var next = state.Views.Where(p => !p.Value.Unsupported &&
                    ((!p.Value.Initialized && !p.Value.Failed) || p.Value.Processed < state.Sequence))
                .OrderBy(p => p.Value.Processed).FirstOrDefault();
            if (next.Key != null)
            {
                ViewDeckChangeWindow target = next.Value;
                View view = views[next.Key];
                try
                {
                    if (view is ViewSheet || view is ViewSchedule ||
                        !FilteredElementCollector.IsViewValidForElementIteration(document, view.Id))
                    { target.Unsupported = true; return; }
                    HashSet<long> members;
                    using (var collector = new FilteredElementCollector(document, view.Id))
                        members = new HashSet<long>(collector.WhereElementIsNotElementType()
                            .Take(MaxMembers + 1).Select(e => e.Id.GetIdLongValue()));
                    bool limited = members.Count > MaxMembers;
                    if (limited) members.Remove(members.Last());
                    target.Scan(members, state.Events, state.Sequence, next.Key == _activeView, limited);
                    foreach (long id in members)
                        if (!state.Snapshots.ContainsKey(id) && state.Queued.Count < MaxSnapshots && state.Queued.Add(id)) state.Prime.Enqueue(id);
                }
                catch (Exception ex)
                { target.Journal.Partial = true; target.Failed = true; Trace.WriteLine("ViewDeck view scan: " + ex); }
                target.Processed = state.Sequence;
            }
            // Lightweight snapshots for deletion category and genuine location translations.
            var budget = Stopwatch.StartNew();
            for (int count = 0; count < 200 && state.Prime.Count > 0 && budget.ElapsedMilliseconds < 20; count++)
            {
                long id = state.Prime.Dequeue();
                state.Queued.Remove(id);
                if (state.Snapshots.ContainsKey(id) || state.Snapshots.Count >= MaxSnapshots) continue;
                try
                {
                    Snapshot snapshot = Capture(document.GetElement(ElementIdExtensions.CreateElementId(id)));
                    if (snapshot != null) state.Snapshots[id] = snapshot;
                }
                catch (Exception ex) { Trace.WriteLine("ViewDeck snapshot: " + ex.Message); }
            }
            long oldest = state.Views.Values.Where(v => !v.Unsupported).Select(v => v.Processed).DefaultIfEmpty(state.Sequence).Min();
            state.Events.RemoveAll(c => c.Sequence <= oldest);
        }

        internal static ViewDeckChangeCounts GetCounts(Document document, View view)
        {
            if (document == null || view == null || !Documents.TryGetValue(document, out DocumentState state) ||
                !state.Views.TryGetValue(view.UniqueId, out ViewDeckChangeWindow target)) return null;
            // No status prose or false zero when there is nothing useful to show.
            if (target.Unsupported || target.Failed || !target.Initialized || target.Processed < state.Sequence ||
                (Equals(_activeDocument, document) && _activeView == view.UniqueId)) return null;
            return target.Journal.Counts();
        }

        private static Snapshot Capture(Element element)
        {
            if (element == null) return null;
            try
            {
                var result = new Snapshot { Category = element.Category?.Name ?? "Éléments" };
                if (element.Location is LocationPoint point) result.Location = Coordinates(point.Point, point.Point);
                else if (element.Location is LocationCurve curve && curve.Curve != null && curve.Curve.IsBound)
                    result.Location = Coordinates(curve.Curve.GetEndPoint(0), curve.Curve.GetEndPoint(1));
                return result;
            }
            catch { return null; }
        }

        private static double[] Coordinates(XYZ start, XYZ end) => new[] { start.X, start.Y, start.Z, end.X, end.Y, end.Z };

        internal static void Clear() { Documents.Clear(); _activeDocument = null; _activeView = null; }
    }
}

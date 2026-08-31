using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.ViewHover
{
    internal enum ViewDeckChangeKind { Added, Modified, Moved, Deleted }

    internal sealed class ViewDeckChangeCounts
    {
        internal int Added, Modified, Deleted;
        internal bool Partial;
    }

    internal sealed class ViewDeckChange
    {
        internal long Sequence;
        internal long Id;
        internal string Category;
        internal ViewDeckChangeKind Kind;

        internal static bool IsTranslation(double[] before, double[] after)
        {
            if (before == null || after == null || before.Length != 6 || after.Length != 6) return false;
            double distance = 0, mismatch = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double delta = after[axis] - before[axis];
                double difference = after[axis + 3] - before[axis + 3] - delta;
                distance += delta * delta;
                mismatch += difference * difference;
            }
            const double tolerance = 1.0 / 304.8; // One millimetre in internal feet.
            return distance > tolerance * tolerance && mismatch < tolerance * tolerance;
        }
    }

    internal sealed class ViewDeckChangeWindow
    {
        internal readonly ViewDeckChangeJournal Journal = new ViewDeckChangeJournal();
        internal HashSet<long> Members = new HashSet<long>();
        internal long Baseline, Processed = -1;
        internal bool Visited, Initialized, Unsupported, Failed;

        internal void Visit(long sequence) { Acknowledge(sequence); Visited = true; }
        internal void Acknowledge(long sequence) { Baseline = sequence; Journal.Clear(); }

        internal void Scan(HashSet<long> members, IEnumerable<ViewDeckChange> changes, long sequence, bool active, bool limited)
        {
            if (limited || (!Initialized && sequence > Baseline)) Journal.Partial = true;
            if (!active)
                foreach (ViewDeckChange change in changes.Where(c => c.Sequence > Processed && c.Sequence > Baseline))
                    Journal.Apply(change, Members, members);
            Members = members;
            Processed = sequence;
            Initialized = true;
            Failed = false;
        }
    }

    // UI/API-independent accumulator. Counts distinct elements, not transactions.
    internal sealed class ViewDeckChangeJournal
    {
        private const int Limit = 1000;
        private readonly Dictionary<long, ViewDeckChange> _changes = new Dictionary<long, ViewDeckChange>();
        internal bool Partial { get; set; }
        internal int Count => _changes.Count;

        internal void Clear() { _changes.Clear(); Partial = false; }

        internal ViewDeckChangeCounts Counts() => new ViewDeckChangeCounts
        {
            Added = _changes.Values.Count(c => c.Kind == ViewDeckChangeKind.Added),
            Modified = _changes.Values.Count(c => c.Kind == ViewDeckChangeKind.Modified || c.Kind == ViewDeckChangeKind.Moved),
            Deleted = _changes.Values.Count(c => c.Kind == ViewDeckChangeKind.Deleted),
            Partial = Partial
        };

        internal void Apply(ViewDeckChange change, ISet<long> before, ISet<long> after)
        {
            // A moved/deleted element can have left this view: keep OLD membership.
            if (!before.Contains(change.Id) && !after.Contains(change.Id)) return;
            if (_changes.TryGetValue(change.Id, out ViewDeckChange previous))
            {
                if (previous.Kind == ViewDeckChangeKind.Added && change.Kind == ViewDeckChangeKind.Deleted)
                { _changes.Remove(change.Id); return; }
                ViewDeckChangeKind kind = change.Kind;
                if (previous.Kind == ViewDeckChangeKind.Added) kind = ViewDeckChangeKind.Added;
                else if (previous.Kind == ViewDeckChangeKind.Deleted && kind == ViewDeckChangeKind.Added)
                    kind = ViewDeckChangeKind.Modified; // Undo/restoration is not a new element since the visit.
                else if (previous.Kind == ViewDeckChangeKind.Moved && kind == ViewDeckChangeKind.Modified)
                    kind = ViewDeckChangeKind.Moved;
                _changes[change.Id] = new ViewDeckChange { Id = change.Id, Category = change.Category, Kind = kind };
            }
            else if (_changes.Count < Limit)
                _changes[change.Id] = new ViewDeckChange { Id = change.Id, Category = change.Category, Kind = change.Kind };
            else Partial = true;
        }

        internal string Summary()
        {
            if (Count == 0) return Partial ? "Suivi partiel — bilan indisponible" : "Aucun changement détecté";
            int added = _changes.Values.Count(c => c.Kind == ViewDeckChangeKind.Added);
            int changed = _changes.Values.Count(c => c.Kind == ViewDeckChangeKind.Modified || c.Kind == ViewDeckChangeKind.Moved);
            int deleted = _changes.Values.Count(c => c.Kind == ViewDeckChangeKind.Deleted);
            var counts = new List<string>();
            if (added > 0) counts.Add("+ " + added);
            if (changed > 0) counts.Add("~ " + changed);
            if (deleted > 0) counts.Add("− " + deleted);
            return string.Join("   ", counts) + (Partial ? " · partiel" : "") + " · impact potentiel";
        }

        internal string Details()
        {
            var groups = _changes.Values.GroupBy(c => c.Category ?? "Éléments")
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture).ToList();
            var lines = new List<string>();
            foreach (var category in groups.Take(3))
            {
                var actions = category.GroupBy(c => c.Kind).OrderBy(g => g.Key)
                    .Select(g => g.Count() + " " + ActionLabel(g.Key, g.Count()));
                lines.Add(category.Key + " : " + string.Join(", ", actions));
            }
            if (groups.Count > 3) lines.Add("+ " + (groups.Count - 3) + " autres catégories");
            return string.Join("\n", lines);
        }

        private static string ActionLabel(ViewDeckChangeKind kind, int count)
        {
            string label = kind == ViewDeckChangeKind.Added ? "ajout" : kind == ViewDeckChangeKind.Deleted ? "suppression" :
                kind == ViewDeckChangeKind.Moved ? "déplacement" : "modification";
            return label + (count > 1 ? "s" : "");
        }
    }
}

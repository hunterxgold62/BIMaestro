using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.PropertiesAppearance
{
    internal static class PropertiesPaletteLayout
    {
        private sealed class NativeRow
        {
            internal int Token;
            internal string Label, Value, Marker;
            internal PropertyPreviewRow Preview;
        }
        private sealed class GroupBlock
        {
            internal string Key;
            internal List<int> Prefix = new List<int>();
            internal List<Tuple<string, List<int>>> Parameters = new List<Tuple<string, List<int>>>();
        }
        private static IntPtr _window, _core;
        private static string _context;
        private static List<NativeRow> _baseline;
        private static List<int> _current;
        private static List<GroupBlock> _groups;
        private static List<int> _prefix;
        private static bool _failed;
        // MoveRows preserves the visible cells but Revit can rebuild the backing editor
        // mapping on a click. Do not enable for users until edit-after-move is qualified.
        private static readonly bool NativeEditingQualified = false;
        internal static bool HasApplied => _baseline != null && _current != null && !_current.SequenceEqual(_baseline.Select(r => r.Token));
        internal static string Status { get; private set; }

        internal static List<PropertyPreviewRow> OriginalOrder(List<PropertyPreviewRow> rows, string signature)
        {
            if (_baseline == null || _context != signature) return rows;
            var rank = _baseline.Where(r => r.Preview != null).Select(r => r.Preview.OrderKey).ToArray();
            return AppearanceOrdering.Ranked(rows, rank, r => r.OrderKey);
        }
        private static List<NativeRow> Read(NativePropertiesGrid grid)
        {
            var rows = new List<NativeRow>();
            for (uint i = 1; i <= grid.RowCount; i++) rows.Add(new NativeRow { Token = (int)i - 1, Label = grid.Value(i, 1), Value = grid.Value(i, 2), Marker = grid.Value(i, 3) });
            return rows;
        }
        private static string Identity(NativeRow row) => row.Label + "\t" + row.Marker;
        private static void Capture(NativePropertiesGrid grid, AppearanceContext context, List<NativeRow> rows)
        {
            _window = grid.Window; _core = grid.Core; _context = context.Signature; _baseline = rows; _current = rows.Select(r => r.Token).ToList();
            _groups = new List<GroupBlock>(); _prefix = new List<int>(); GroupBlock group = null; List<int> chunk = _prefix;
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.Marker) && !string.IsNullOrWhiteSpace(row.Label))
                {
                    string key; if (!context.GroupMap.TryGetValue(AppearancePreferences.Normalize(row.Label), out key)) key = "native:" + AppearancePreferences.Normalize(row.Label);
                    group = new GroupBlock { Key = key }; _groups.Add(group); chunk = group.Prefix;
                    row.Preview = new PropertyPreviewRow { Header = true, Label = row.Label, GroupKey = key };
                }
                else if (!string.IsNullOrWhiteSpace(row.Label) && group != null)
                {
                    PropertyTarget parameter; context.ParameterMap.TryGetValue(group.Key + "|" + AppearancePreferences.Normalize(row.Label), out parameter);
                    row.Preview = new PropertyPreviewRow { Label = row.Label, GroupKey = group.Key, ParameterKey = parameter?.Key };
                    chunk = new List<int>(); group.Parameters.Add(Tuple.Create(row.Preview.OrderKey, chunk));
                }
                chunk.Add(row.Token);
            }
        }
        internal static void Apply(NativePropertiesGrid grid, AppearanceContext context, AppearancePreferences preferences, string revitVersion, Action beforeMove)
        {
            Status = null;
            var layout = preferences == null ? new AppearanceLayout() : AppearanceOrdering.Resolve(preferences, context.CategoryKey, context.TypeKey);
            if (layout.IsEmpty && !HasApplied) return;
            // Keep saved layouts available in the comparison without changing native editors.
            if (!NativeEditingQualified && !HasApplied)
            {
                Status = "Ordre enregistré dans l’aperçu ; déplacement dans Revit suspendu pendant la validation des clics.";
                return;
            }
            // MoveRows and native editing are qualified separately from color drawing.
            if (revitVersion != "2025") { Status = "Ordre conservé dans vos préférences ; déplacement natif validé uniquement sur Revit 2025 pour cet essai."; return; }
            if (grid.IsEditing) { Status = "Terminez la saisie dans Revit pour appliquer le nouvel ordre."; return; }
            var actual = Read(grid);
            bool sameInstance = _window == grid.Window && _core == grid.Core && _context == context.Signature && _baseline != null;
            if (!sameInstance) { _failed = false; Capture(grid, context, actual); }
            else
            {
                var expectedLabels = _current.Select(i => Identity(_baseline[i]));
                if (!actual.Select(Identity).SequenceEqual(expectedLabels))
                {
                    if (actual.Select(Identity).SequenceEqual(_baseline.Select(Identity))) _current = _baseline.Select(r => r.Token).ToList();
                    else { _failed = false; Capture(grid, context, actual); }
                }
            }
            if (_failed) { Status = "Ordre natif suspendu pour cette sélection après une réponse inattendue de Revit."; return; }
            var desired = _prefix.ToList();
            foreach (var group in AppearanceOrdering.Ranked(_groups, layout.Groups, g => g.Key))
            {
                desired.AddRange(group.Prefix);
                var parameters = layout.Parameters.TryGetValue(group.Key, out var order) ? AppearanceOrdering.Ranked(group.Parameters, order, p => p.Item1) : group.Parameters;
                foreach (var parameter in parameters) desired.AddRange(parameter.Item2);
            }
            if (desired.Count != _current.Count || desired.Distinct().Count() != _current.Count) throw new InvalidOperationException("Ordre incomplet : aucune ligne native n’est déplacée.");
            int moves = 0; bool notified = false;
            for (int i = 0; i < desired.Count; i++)
            {
                if (_current[i] == desired[i]) continue;
                int from = _current.IndexOf(desired[i]);
                if (from <= i || ++moves > 200) throw new InvalidOperationException("Permutation native non reconnue.");
                int count = 1; while (from + count < _current.Count && i + count < desired.Count && _current[from + count] == desired[i + count]) count++;
                var before = Read(grid); var expected = before.ToList(); var block = expected.GetRange(from, count); expected.RemoveRange(from, count); expected.InsertRange(i, block);
                if (!notified) { beforeMove?.Invoke(); notified = true; }
                if (!grid.MoveRows((uint)from + 1, (uint)(from + count), (uint)i + 1)) { _failed = true; Status = "Revit n’a pas accepté ce déplacement de lignes."; return; }
                var after = Read(grid);
                if (!after.Select(r => r.Label + "\t" + r.Value + "\t" + r.Marker).SequenceEqual(expected.Select(r => r.Label + "\t" + r.Value + "\t" + r.Marker)))
                { _failed = true; throw new InvalidOperationException("Le contrôle natif n’a pas conservé la permutation attendue ; essai suspendu."); }
                var tokens = _current.GetRange(from, count); _current.RemoveRange(from, count); _current.InsertRange(i, tokens);
            }
            if (moves > 0) grid.Redraw();
            Status = HasApplied ? "Ordre personnalisé appliqué à la palette Revit." : null;
        }
        internal static void Forget() { _baseline = null; _current = null; _groups = null; _prefix = null; _failed = false; _context = null; }
    }
}

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
        private static List<int> _prefix, _suffix;
        private static Dictionary<long, int> _parameterTokens;
        private static bool _failed;
        // Wall and view palettes use the same native parameter mapping. Every
        // affected editable row is still checked before and after each movement.
        private static bool NativeEditingQualified(string version, string category)
        {
#if BIMAESTRO_PROPERTIES_TESTS
            return true;
#else
            return (category == "cat:-2000011" || category == "cat:-2000279") &&
                (version == "2023" || version == "2024" || version == "2025" || version == "2026" || version == "2027");
#endif
        }
        internal static bool HasApplied => _baseline != null && _current != null && !_current.SequenceEqual(_baseline.Select(r => r.Token));
        internal static string Status { get; private set; }
        internal static bool RequiresNativeRebuild { get; private set; }

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
        private static string Identity(NativeRow row) => row.Label + "\t" + (NativePropertiesGrid.IsGroupMarker(row.Marker) ? "group" : "parameter");
        private static bool Capture(NativePropertiesGrid grid, AppearanceContext context, List<NativeRow> rows)
        {
            // Revit can publish the new API selection before rebuilding its palette.
            // Build a candidate locally; a transient mismatch must not replace a
            // previously verified baseline or poison the next Idling attempt.
            var current = rows.Select(r => r.Token).ToList();
            var groups = new List<GroupBlock>(); var prefix = new List<int>(); GroupBlock group = null; List<int> chunk = prefix;
            // Revit's final empty row is a visible grid filler, while the empty
            // rows separating groups normally have zero height. The filler must
            // remain at the end, even when the last group/parameter moves first.
            int contentEnd = rows.Count;
            while (contentEnd > 0 && string.IsNullOrWhiteSpace(rows[contentEnd - 1].Label)
                && string.IsNullOrWhiteSpace(rows[contentEnd - 1].Value)
                && string.IsNullOrWhiteSpace(rows[contentEnd - 1].Marker)) contentEnd--;
            var suffix = rows.Skip(contentEnd).Select(r => r.Token).ToList();
            // A palette already ordered by an earlier build may contain that
            // visible filler in the middle. Recognize it by its native height,
            // rather than adopting the displaced filler into a parameter block.
            foreach (var row in rows.Take(contentEnd).Where(r => string.IsNullOrWhiteSpace(r.Label) &&
                string.IsNullOrWhiteSpace(r.Value) && string.IsNullOrWhiteSpace(r.Marker)))
            {
                var bounds = grid.Bounds((uint)row.Token + 1, 2);
                if (bounds.Bottom > bounds.Top) suffix.Add(row.Token);
            }
            var fillers = new HashSet<int>(suffix);
            foreach (var row in rows.Take(contentEnd).Where(r => !fillers.Contains(r.Token)))
            {
                if (NativePropertiesGrid.IsGroupMarker(row.Marker) && !string.IsNullOrWhiteSpace(row.Label))
                {
                    string key; if (!context.GroupMap.TryGetValue(AppearancePreferences.Normalize(row.Label), out key)) key = "native:" + AppearancePreferences.Normalize(row.Label);
                    group = new GroupBlock { Key = key }; groups.Add(group); chunk = group.Prefix;
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
            // Read the parameter identity through Revit's own ElemGrid, independently
            // of the visible label/value. A matching caption alone cannot qualify editing.
            var parameterTokens = new Dictionary<long, int>();
            foreach (long id in context.Parameters.Where(p => p.NativeParameterId.HasValue).Select(p => p.NativeParameterId.Value).Distinct())
            {
                int nativeRow = grid.FindParameterRow(id);
                if (nativeRow > 0)
                {
                    var target = context.Parameters.First(p => p.NativeParameterId == id);
                    if (nativeRow > rows.Count || AppearancePreferences.Normalize(rows[nativeRow - 1].Label) != target.NativeName)
                    {
                        Status = "En attente de la synchronisation de la sélection avec la palette Revit.";
                        return false;
                    }
                    parameterTokens.Add(id, nativeRow - 1);
                }
            }
            if (parameterTokens.Count == 0)
            {
                Status = "En attente de la synchronisation des identités de paramètres dans la palette Revit.";
                return false;
            }
            _window = grid.Window; _core = grid.Core; _context = context.Signature;
            _baseline = rows; _current = current; _groups = groups; _prefix = prefix; _suffix = suffix; _parameterTokens = parameterTokens;
            _failed = false; RequiresNativeRebuild = false;
            return true;
        }
        private static bool ParameterMappingMatches(NativePropertiesGrid grid, IList<int> order)
        {
            return _parameterTokens != null && _parameterTokens.Count > 0 && _parameterTokens.All(p => grid.FindParameterRow(p.Key) == order.IndexOf(p.Value) + 1);
        }
        private static bool SameCells(List<NativeRow> left, List<NativeRow> right) =>
            left.Select(r => r.Label + "\t" + r.Value + "\t" + r.Marker).SequenceEqual(right.Select(r => r.Label + "\t" + r.Value + "\t" + r.Marker));
        private static void RestoreBaseline(NativePropertiesGrid grid, Action beforeMove)
        {
            if (HasApplied) beforeMove?.Invoke();
            using (grid.DeferDrawing())
            {
                for (int i = 0; i < _current.Count; i++)
                {
                    if (_current[i] == i) continue;
                    int from = _current.IndexOf(i);
                    var before = Read(grid); var expected = before.ToList();
                    var row = expected[from]; expected.RemoveAt(from); expected.Insert(i, row);
                    // A failed native call can still have moved rows. Keep recovery armed
                    // until both the complete rollback and parameter identities are checked.
                    RequiresNativeRebuild = true;
                    if (!grid.MoveRows((uint)from + 1, (uint)from + 1, (uint)i + 1) || !SameCells(Read(grid), expected))
                        throw new InvalidOperationException("La palette doit être reconstruite par Revit après un déplacement inattendu.");
                    int token = _current[from]; _current.RemoveAt(from); _current.Insert(i, token);
                }
                if (!ParameterMappingMatches(grid, _current)) throw new InvalidOperationException("La palette doit être reconstruite pour rétablir les identités des paramètres.");
                RequiresNativeRebuild = false;
            }
        }
        internal static void Apply(NativePropertiesGrid grid, AppearanceContext context, AppearancePreferences preferences, string revitVersion, Action beforeMove)
        {
            Status = null;
            var layout = preferences == null ? new AppearanceLayout() : AppearanceOrdering.Resolve(preferences, context.CategoryKey, context.TypeKey);
            if (layout.IsEmpty && !HasApplied) return;
            // Keep saved layouts available in the comparison without changing native editors.
            if (!NativeEditingQualified(revitVersion, context.CategoryKey))
            {
                Status = "Ordre enregistré dans l’aperçu ; cette version ou catégorie n’est pas encore validée pour la saisie après déplacement.";
                return;
            }
            // MoveRows and native editing are qualified separately from color drawing.
            if (!int.TryParse(revitVersion, out int year) || year < 2023 || year > 2027) { Status = "Réordonnancement indisponible pour cette version de Revit."; return; }
            if (grid.IsEditing) { Status = "Terminez la saisie dans Revit pour appliquer le nouvel ordre."; return; }
            var actual = Read(grid);
            bool sameInstance = _window == grid.Window && _core == grid.Core && _context == context.Signature && _baseline != null;
            if (!sameInstance)
            {
                bool retainedOrder = _baseline != null && _context == context.Signature &&
                    actual.Select(Identity).SequenceEqual(_current.Select(i => Identity(_baseline[i]))) &&
                    ParameterMappingMatches(grid, _current);
                if (retainedOrder) { _window = grid.Window; _core = grid.Core; }
                else if (!Capture(grid, context, actual)) return;
            }
            else
            {
                var expectedLabels = _current.Select(i => Identity(_baseline[i]));
                if (!actual.Select(Identity).SequenceEqual(expectedLabels))
                {
                    if (actual.Select(Identity).SequenceEqual(_baseline.Select(Identity))) _current = _baseline.Select(r => r.Token).ToList();
                    else { if (!Capture(grid, context, actual)) return; }
                }
            }
            if (_failed) { Status = "Ordre natif suspendu pour cette sélection après une réponse inattendue de Revit."; return; }
            if (!ParameterMappingMatches(grid, _current)) { _failed = true; RequiresNativeRebuild = HasApplied; throw new InvalidOperationException("La correspondance native des paramètres a changé avant le déplacement."); }
            var desired = _prefix.ToList();
            foreach (var group in AppearanceOrdering.Ranked(_groups, layout.Groups, g => g.Key))
            {
                desired.AddRange(group.Prefix);
                var parameters = layout.Parameters.TryGetValue(group.Key, out var order) ? AppearanceOrdering.Ranked(group.Parameters, order, p => p.Item1) : group.Parameters;
                foreach (var parameter in parameters) desired.AddRange(parameter.Item2);
            }
            desired.AddRange(_suffix);
            if (desired.Count != _current.Count || desired.Distinct().Count() != _current.Count) throw new InvalidOperationException("Ordre incomplet : aucune ligne native n’est déplacée.");
            if (_current.SequenceEqual(desired)) { Status = HasApplied ? "Ordre personnalisé appliqué à la palette Revit." : null; return; }
            int moves = 0; bool notified = false;
            using (grid.DeferDrawing())
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    if (_current[i] == desired[i]) continue;
                    int from = _current.IndexOf(desired[i]);
                    if (from <= i || ++moves > 200) throw new InvalidOperationException("Permutation native non reconnue.");
                    int count = 1; while (from + count < _current.Count && i + count < desired.Count && _current[from + count] == desired[i + count]) count++;
                    var verifiedTokens = new HashSet<int>(_parameterTokens.Values);
                    for (int affected = i; affected < from + count; affected++)
                    {
                        var native = _baseline[_current[affected]];
                        if (!string.IsNullOrWhiteSpace(native.Label) && !NativePropertiesGrid.IsGroupMarker(native.Marker) && grid.CanStyle((uint)affected + 1) && !verifiedTokens.Contains(native.Token))
                        {
                            RestoreBaseline(grid, notified ? null : beforeMove); _failed = true;
                            Status = "Ordre suspendu : un champ modifiable de cette sélection n’a pas d’identité native vérifiable.";
                            return;
                        }
                    }
                    var before = Read(grid); var expected = before.ToList(); var block = expected.GetRange(from, count); expected.RemoveRange(from, count); expected.InsertRange(i, block);
                    if (!notified) { beforeMove?.Invoke(); notified = true; }
                    RequiresNativeRebuild = true;
                    if (!grid.MoveRows((uint)from + 1, (uint)(from + count), (uint)i + 1)) { _failed = true; throw new InvalidOperationException("Revit n’a pas accepté ce déplacement de lignes."); }
                    var after = Read(grid);
                    if (!SameCells(after, expected))
                    { _failed = true; throw new InvalidOperationException("Le contrôle natif n’a pas conservé la permutation attendue ; essai suspendu."); }
                    var tokens = _current.GetRange(from, count); _current.RemoveRange(from, count); _current.InsertRange(i, tokens);
                    if (!ParameterMappingMatches(grid, _current))
                    {
                        _failed = true;
                        RestoreBaseline(grid, null);
                        throw new InvalidOperationException("La correspondance entre lignes et paramètres Revit a changé ; réordonnancement suspendu.");
                    }
                    RequiresNativeRebuild = false;
                }
            }
            Status = HasApplied ? "Ordre personnalisé appliqué à la palette Revit." : null;
        }
        internal static void Forget() { _baseline = null; _current = null; _groups = null; _prefix = null; _suffix = null; _parameterTokens = null; _failed = false; _context = null; RequiresNativeRebuild = false; }
    }
}

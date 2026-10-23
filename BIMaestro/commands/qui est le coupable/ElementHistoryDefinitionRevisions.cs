using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Analyse
{
    // Session revisions observe transactions, not Element.VersionGuid (which
    // can remain unchanged between saves). All document access stays on Revit.
    internal static class ElementHistoryDefinitionRevisions
    {
        private sealed class State
        {
            internal Document Document;
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal long Revision;
            internal readonly Dictionary<string, long> Changed = new Dictionary<string, long>(StringComparer.Ordinal);
            internal readonly Dictionary<int, string> KnownIds = new Dictionary<int, string>();
        }
        private static readonly List<State> States = new List<State>();
        private static State For(Document doc)
        {
            var state = States.FirstOrDefault(s => ReferenceEquals(s.Document, doc));
            if (state == null) state = States.FirstOrDefault(s => s.Document.IsValidObject && doc.IsValidObject && s.Document.Equals(doc));
            if (state == null) { state = new State { Document = doc }; States.Add(state); }
            return state;
        }
        internal static string SessionId(Document doc) => For(doc).Id;
        internal static long Revision(Document doc) => For(doc).Revision;
        internal static void Remember(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            var state = For(doc);
            if (state.KnownIds.ContainsKey(id.GetIdValue())) return;
            var element = doc.GetElement(id);
            if (element == null || !IsDefinition(element)) return;
            state.KnownIds[id.GetIdValue()] = element.UniqueId;
            if (element is FamilySymbol symbol) state.KnownIds[symbol.Family.Id.GetIdValue()] = symbol.Family.UniqueId;
        }
        private static bool IsDefinition(Element element) => element is ElementType || element is Family
            || element is Material || element is ParameterElement || element is Level || element is Phase
            || element is GraphicsStyle;
        internal static void Changed(Document doc, IEnumerable<ElementId> changed, IEnumerable<ElementId> deleted)
        {
            var state = For(doc);
            var definitions = changed.Select(doc.GetElement).Where(e => e != null && IsDefinition(e)).ToList();
            var removed = deleted.Select(id => id.GetIdValue()).Where(state.KnownIds.ContainsKey).ToList();
            if (definitions.Count == 0 && removed.Count == 0) return;
            state.Revision++;
            foreach (var element in definitions)
            { state.KnownIds[element.Id.GetIdValue()] = element.UniqueId; state.Changed[element.UniqueId] = state.Revision; }
            foreach (var id in removed) { state.Changed[state.KnownIds[id]] = state.Revision; state.KnownIds.Remove(id); }
        }
        internal static bool TrySource(string sessionId, long capturedRevision, out Document document)
        {
            var state = States.FirstOrDefault(s => s.Id == sessionId && s.Document.IsValidObject && capturedRevision >= 0 && capturedRevision <= s.Revision);
            document = state?.Document; return document != null;
        }
        internal static bool Unchanged(string sessionId, long capturedRevision, string uniqueId)
        {
            var state = States.FirstOrDefault(s => s.Id == sessionId);
            if (state == null || capturedRevision < 0 || capturedRevision > state.Revision) return false;
            return !state.Changed.TryGetValue(uniqueId, out var changed) || changed <= capturedRevision;
        }
        internal static string Version(string sessionId, long revision)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sessionId + "|definitions|" + revision))).Replace("-", "").ToLowerInvariant();
        }
    }
}

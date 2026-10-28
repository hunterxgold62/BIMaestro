using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        // Read-only index: deleting another referenced element can release an
        // alignment or angular constraint even though its owner is still kept.
        // Unlocked dimensions are included deliberately; the caller must account
        // for the possibly large transitive context before publishing an archive.
        private static Dictionary<ElementId, List<ElementId>> BuildNativeConstraintContext(
            Document source, List<string> warnings)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var peers = new Dictionary<ElementId, HashSet<ElementId>>();
            foreach (var dimension in new FilteredElementCollector(source)
                .OfClass(typeof(Dimension)).Cast<Dimension>().Where(element => !(element is SpotDimension)))
            {
                var identity = dimension.UniqueId;
                List<ElementId> references;
                try
                {
                    // Finish reading this dimension before changing the index:
                    // a failed References read must not leave a partial relation.
                    var ids = new HashSet<ElementId>();
                    foreach (Reference reference in dimension.References)
                    {
                        var id = reference?.ElementId;
                        if (id == null || id == ElementId.InvalidElementId) continue;
                        var element = source.GetElement(id);
                        if (element == null || !element.IsValidObject) continue;
                        ids.Add(element.Id);
                    }
                    references = ids.ToList();
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception failure)
                {
                    var warning = "Contrainte native non lue : " + identity + " : " + failure.Message;
                    warnings?.Add(warning);
                    Trace.WriteLine(warning);
                    continue;
                }
                foreach (var id in references)
                {
                    if (!peers.TryGetValue(id, out var related))
                        peers.Add(id, related = new HashSet<ElementId>());
                    foreach (var peer in references)
                        if (peer != id) related.Add(peer);
                }
            }
            return peers.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        }
    }
}
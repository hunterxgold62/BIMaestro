using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryRestoration
    {
        // Restored ends sit exactly where they were. Revit's own end gap is far
        // smaller; anything beyond a few millimetres is not the same joint.
        private const double CoincidentEndTolerance = 0.005;

        // Connect every free end of the restored MEP objects to a coincident,
        // facing, same-size free end, restored or already in the model. Revit then
        // merges the members into the surrounding system on its own.
        private static HistoryRestoreBatch ConnectRestoredByGeometry(Document doc, HistoryRestoreBatch batch)
        {
            if (doc == null || batch == null) return batch;
            var restored = batch.Items.Where(i => (i.Created || i.Repaired) && !string.IsNullOrWhiteSpace(i.UniqueId))
                .Select(i => doc.GetElement(i.UniqueId)).Where(e => e != null && !(e is MEPSystem))
                .GroupBy(e => e.Id).Select(g => g.First()).ToList();
            if (!restored.Any(e => FreeEnds(e).Any())) return batch;
            using (var group = new TransactionGroup(doc, "BIMaestro - Raccorder éléments restaurés"))
            {
                group.Start();
                int connected = 0;
                foreach (var element in restored)
                    foreach (var end in FreeEnds(element).ToList())
                    {
                        if (end.IsConnected) continue; // joined from its peer earlier in this pass
                        var peer = FindCoincidentEnd(doc, element, end);
                        if (peer == null) continue;
                        using (var tx = new Transaction(doc, "BIMaestro - Raccorder élément restauré"))
                        {
                            tx.Start();
                            var failures = new RestoreFailures();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                            try
                            {
                                end.ConnectTo(peer);
                                doc.Regenerate();
                                if (!end.IsConnectedTo(peer)) throw new InvalidOperationException("Raccord refusé par Revit.");
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(failures.Message ?? "Raccord refusé par Revit.");
                                connected++;
                            }
                            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                            catch (Exception ex)
                            {
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                                batch.ConnectionFailures.Add(ElementLabel(element) + " : " + ex.Message);
                            }
                        }
                    }
                if (connected > 0 && group.Assimilate() == TransactionStatus.Committed) batch.ConnectionsRestored += connected;
                else if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            }
            return batch;
        }

        private static IEnumerable<Connector> FreeEnds(Element element)
            => ElementHistoryNetwork.Ports(element).Where(c => c.ConnectorType == ConnectorType.End && !c.IsConnected
                && (c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac || c.Domain == Domain.DomainCableTrayConduit));

        private static Connector FindCoincidentEnd(Document doc, Element owner, Connector end)
        {
            XYZ origin;
            try { origin = end.Origin; } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
            var reach = new XYZ(CoincidentEndTolerance, CoincidentEndTolerance, CoincidentEndTolerance);
            var nearby = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(origin - reach, origin + reach)))
                .Where(e => e.Id != owner.Id && !(e is MEPSystem));
            return nearby.SelectMany(FreeEnds).FirstOrDefault(peer => peer.Domain == end.Domain && peer.Shape == end.Shape
                && peer.Origin.DistanceTo(origin) <= CoincidentEndTolerance && Facing(end, peer) && SameSize(end, peer));
        }

        private static bool Facing(Connector a, Connector b)
        {
            try { return a.CoordinateSystem.BasisZ.DotProduct(b.CoordinateSystem.BasisZ) < -0.98; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return true; } // no frame: position decides
        }

        private static bool SameSize(Connector a, Connector b)
        {
            const double tolerance = 1e-4;
            try
            {
                return a.Shape == ConnectorProfileType.Round ? Math.Abs(a.Radius - b.Radius) < tolerance
                    : Math.Abs(a.Width - b.Width) < tolerance && Math.Abs(a.Height - b.Height) < tolerance;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return true; }
        }

        private static string ElementLabel(Element element)
            => (element.Name ?? element.GetType().Name) + " [" + element.Id + "]";
    }
}

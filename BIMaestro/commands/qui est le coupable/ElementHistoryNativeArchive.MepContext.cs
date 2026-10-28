using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        // Export-only closure. A native capsule needs the complete physical
        // network, including disconnected components of each logical system.
        // This must never expand the set pasted into the user's project.
        private static List<Element> ExpandNativeMepContext(Document source, List<Element> selected)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var enumerable = new HashSet<ElementId>(new FilteredElementCollector(source)
                .WhereElementIsNotElementType().ToElementIds());
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var systems = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<Element>();
            var result = new List<Element>();

            void Add(Element element)
            {
                if (element == null || !element.IsValidObject || !enumerable.Contains(element.Id)
                    || element is ElementType || element is MEPSystem || IsCalo(element)
                    || ElementHistoryExternalModels.IsExcluded(element)) return;
                var original = source.GetElement(element.Id);
                if (original == null || original.UniqueId != element.UniqueId)
                    throw new InvalidOperationException("Contexte MEP natif : un objet n’appartient pas à la copie source.");
                if (!visited.Add(original.UniqueId)) return;
                result.Add(original); pending.Enqueue(original);
            }

            foreach (var element in selected ?? new List<Element>()) Add(element);
            while (pending.Count > 0)
            {
                var owner = pending.Dequeue();
                try
                {
                    foreach (var port in ElementHistoryNetwork.Ports(owner))
                    {
                        var domain = port.Domain;
                        // Physical trays/conduits may be followed, but do not
                        // interpret electrical circuits as pipe/HVAC systems.
                        if (domain != Domain.DomainPiping && domain != Domain.DomainHvac
                            && domain != Domain.DomainCableTrayConduit) continue;
                        if (domain == Domain.DomainPiping || domain == Domain.DomainHvac)
                        {
                            var system = port.MEPSystem;
                            if (system != null && systems.Add(system.UniqueId))
                            {
                                IEnumerable<Element> members;
                                if (system is PipingSystem piping) members = piping.PipingNetwork.Cast<Element>();
                                else if (system is MechanicalSystem mechanical) members = mechanical.DuctNetwork.Cast<Element>();
                                else throw new InvalidOperationException("Le système d’un connecteur hydraulique ou aéraulique a une classe inattendue.");
                                foreach (var member in members.OrderBy(element => element.UniqueId, StringComparer.Ordinal)) Add(member);
                            }
                        }
                        foreach (var peer in port.AllRefs.Cast<Connector>())
                        {
                            var peerOwner = peer.Owner;
                            if (peerOwner == null || peerOwner.Id == owner.Id || peerOwner is MEPSystem
                                || peerOwner is ElementType || !enumerable.Contains(peerOwner.Id)
                                || peer.Domain != domain || !port.IsConnectedTo(peer)) continue;
                            Add(peerOwner);
                        }
                    }
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception failure)
                {
                    throw new InvalidOperationException("Lecture du réseau natif impossible pour " + owner.UniqueId + " : " + failure.Message, failure);
                }
            }
            return result;
        }
    }
}

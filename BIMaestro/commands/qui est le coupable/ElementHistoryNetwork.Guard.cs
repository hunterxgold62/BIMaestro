using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNetwork
    {
        private static bool HistoricalFamilyFrameMatches(Element element, HistoryRecipe recipe, out string failure)
            => ElementHistoryReconstruction.VerifyHistoricalPlacement(element, recipe, out failure, includeBoundingBox: false);

        private static string ReadPortClassification(Connector connector)
            => connector.Domain == Domain.DomainPiping ? connector.PipeSystemType.ToString()
                : connector.Domain == Domain.DomainHvac ? connector.DuctSystemType.ToString() : null;

        private static MEPSystemType ReadPortSystemType(Connector connector)
        {
            if (connector.Domain != Domain.DomainPiping && connector.Domain != Domain.DomainHvac) return null;
            var system = connector.MEPSystem;
            return system == null ? ElementHistoryNativeArchive.ReadMepSystemType(connector.Owner) as MEPSystemType
                : connector.Owner.Document.GetElement(system.GetTypeId()) as MEPSystemType;
        }

        private static bool IsConcreteClassification(string classification) => !string.IsNullOrWhiteSpace(classification)
            && classification != "UndefinedSystemType" && classification != "UndefinedSystemClassification"
            && classification != "Global" && classification != "Fitting";

        private static bool IsExcludedInsulation(Element element) => element is PipeInsulation || element is DuctInsulation
            || (element != null && ElementHistoryRestoration.IsCalorifuge(element.Category?.Name,
                element.Name + " " + (element is FamilyInstance family ? family.Symbol?.Family?.Name : "")));

        private static bool IsSizingSystem(MEPSystemType type, HistoryPort port) => type != null
            && (port.Domain == (int)Domain.DomainPiping ? type is PipingSystemType : type is MechanicalSystemType)
            && (!IsConcreteClassification(port.Classification)
                || string.Equals(type.SystemClassification.ToString(), port.Classification, StringComparison.Ordinal));

        internal static bool HistoricalSystemTypeMatches(Element actual, string sourceUniqueId, string version)
        {
            if (actual == null || string.IsNullOrWhiteSpace(sourceUniqueId)) return false;
            // The unchanged original project type has no capsule version marker.
            // Version + origin identify only transported copies whose runtime UID differs.
            if (actual.UniqueId == sourceUniqueId) return true;
            if (string.IsNullOrWhiteSpace(version)) return false;
            return ElementHistoryNativeArchive.ReadHistoricalTypeVersion(actual) == version
                && (ElementHistoryRestoration.GetOrigins(actual) ?? new List<string>()).Contains(sourceUniqueId);
        }

        private static ElementId SizingSystemType(Document doc, HistoryPort port, HistoryRecipe recipe)
        {
            var uids = new HashSet<string>(StringComparer.Ordinal);
            void Add(string uid) { if (!string.IsNullOrWhiteSpace(uid)) uids.Add(uid); }
            Add(port.SystemType);
            foreach (var link in recipe.Connections ?? new List<HistoryConnection>())
            {
                if (link.Port?.Point == null || port.Point == null || link.Port.Domain != port.Domain
                    || Vector(link.Port.Point).DistanceTo(Vector(port.Point)) > 1e-5) continue;
                Add(link.Port.SystemType);
                Add(link.PeerPort?.SystemType);
                // Legacy histories did not retain system UIDs on their ports.
                // An identified historical neighbour is evidence; the project's
                // first available system type is not.
                if (string.IsNullOrWhiteSpace(link.PeerPort?.SystemType))
                    Add(ElementHistoryNativeArchive.ReadMepSystemType(FindHistoricalCurve(doc, link.Peer))?.UniqueId);
            }
            var exact = uids.Select(uid => ElementHistoryReconstruction.FindOriginal(doc, uid) as MEPSystemType)
                .Where(type => IsSizingSystem(type, port)).GroupBy(type => type.UniqueId).Select(group => group.First()).ToList();
            if (!string.IsNullOrWhiteSpace(port.SystemVersion))
                exact = new FilteredElementCollector(doc).OfClass(port.Domain == (int)Domain.DomainPiping
                    ? typeof(PipingSystemType) : typeof(MechanicalSystemType)).Cast<MEPSystemType>()
                    .Where(type => IsSizingSystem(type, port) && uids.Any(uid => HistoricalSystemTypeMatches(type, uid, port.SystemVersion))).ToList();
            if (!string.IsNullOrWhiteSpace(port.SystemType))
            {
                var retained = exact.SingleOrDefault(type => HistoricalSystemTypeMatches(type, port.SystemType, port.SystemVersion));
                if (retained != null) return retained.Id;
                throw new InvalidOperationException("Le type de système historique du connecteur est absent ou de classification différente : " + port.SystemType);
            }
            if (exact.Count == 1) return exact[0].Id;
            if (exact.Count > 1) throw new InvalidOperationException("Plusieurs types de système historiques contradictoires pour le raccord.");
            if (IsConcreteClassification(port.Classification))
            {
                var types = new FilteredElementCollector(doc).OfClass(port.Domain == (int)Domain.DomainPiping
                    ? typeof(PipingSystemType) : typeof(MechanicalSystemType)).Cast<MEPSystemType>()
                    .Where(type => IsSizingSystem(type, port)).ToList();
                if (types.Count == 1) return types[0].Id;
            }
            throw new InvalidOperationException("Type de système du raccord non identifié de manière unique ; aucun type arbitraire ne sera utilisé.");
        }

        internal sealed class ConnectionGuard
        {
            internal readonly List<NetworkPlacement> States = new List<NetworkPlacement>();
            internal readonly List<Tuple<string, HistoryPort>> HistoricalEndpoints = new List<Tuple<string, HistoryPort>>();
            internal readonly List<HistoryConnection> ExistingConnections = new List<HistoryConnection>();
        }

        internal sealed class NetworkPlacement
        {
            internal string UniqueId;
            internal ElementId TypeId;
            internal XYZ Point, Origin, BasisX, BasisY, BasisZ;
            internal bool? Mirrored, HandFlipped, FacingFlipped;
            internal List<XYZ> CurvePoints, FlexiblePoints;
            internal List<HistoryPort> Ports;
            internal bool AllowUndefinedSystemNormalization;
            internal string SystemType;
        }

        internal static ConnectionGuard CaptureConnectionGuard(Document doc, Element first, Element second,
            HistoryRecipe firstRecipe = null, HistoryRecipe secondRecipe = null,
            HistoryPort firstHistoricalPort = null, HistoryPort secondHistoricalPort = null)
        {
            var guard = new ConnectionGuard();
            var owners = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var root in new[] { first, second }.Where(element => element != null))
            {
                owners[root.UniqueId] = root;
                foreach (var port in Ports(root))
                foreach (var peer in port.AllRefs.Cast<Connector>())
                    if (peer.Owner.Id != root.Id && !(peer.Owner is MEPSystem) && !IsExcludedInsulation(peer.Owner)
                        && IsPhysicalPort(peer) && port.IsConnectedTo(peer)) owners[peer.Owner.UniqueId] = peer.Owner;
            }
            foreach (var owner in owners.Values)
            {
                var recipe = owner.UniqueId == first?.UniqueId ? firstRecipe : owner.UniqueId == second?.UniqueId ? secondRecipe : null;
                guard.States.Add(CaptureNetworkPlacement(owner, recipe));
            }
            // A solver can disconnect another port without moving either owner.
            // Preserve every existing physical edge of the guarded owners,
            // including a direct neighbour's edge towards the rest of the network.
            var edges = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in owners.Values.Where(element => !IsExcludedInsulation(element)))
            foreach (var port in Ports(owner))
            foreach (var peer in port.AllRefs.Cast<Connector>())
            {
                if (peer.Owner.Id == owner.Id || peer.Owner is MEPSystem || IsExcludedInsulation(peer.Owner)
                    || !IsPhysicalPort(peer) || !port.IsConnectedTo(peer)) continue;
                var a = owner.UniqueId + ":" + port.Id;
                var b = peer.Owner.UniqueId + ":" + peer.Id;
                if (!edges.Add(string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a)) continue;
                guard.ExistingConnections.Add(new HistoryConnection { SourceMember = owner.UniqueId,
                    Port = CapturePort(port), Peer = peer.Owner.UniqueId, PeerPort = CapturePort(peer) });
            }
            if (first != null && firstHistoricalPort != null) guard.HistoricalEndpoints.Add(Tuple.Create(first.UniqueId, firstHistoricalPort));
            if (second != null && secondHistoricalPort != null) guard.HistoricalEndpoints.Add(Tuple.Create(second.UniqueId, secondHistoricalPort));
            return guard;
        }

        private static bool IsPhysicalPort(Connector connector) => connector.ConnectorType == ConnectorType.End
            || connector.ConnectorType == ConnectorType.Curve || connector.ConnectorType == ConnectorType.Physical;

        private static NetworkPlacement CaptureNetworkPlacement(Element element, HistoryRecipe recipe)
        {
            var state = new NetworkPlacement
            {
                UniqueId = element.UniqueId, TypeId = element.GetTypeId(),
                Point = (element.Location as LocationPoint)?.Point, Ports = Ports(element).Select(CapturePort).ToList(),
                SystemType = ElementHistoryNativeArchive.ReadMepSystemType(element)?.UniqueId,
                AllowUndefinedSystemNormalization = (recipe?.Native ?? recipe?.OpeningFallback)?.SourceSystemTypeDefined == false
            };
            if (element is FamilyInstance family)
            {
                var transform = family.GetTransform();
                state.Origin = transform.Origin; state.BasisX = transform.BasisX; state.BasisY = transform.BasisY; state.BasisZ = transform.BasisZ;
                state.Mirrored = family.Mirrored; state.HandFlipped = family.HandFlipped; state.FacingFlipped = family.FacingFlipped;
            }
            if (element.Location is LocationCurve location)
                state.CurvePoints = new List<XYZ> { location.Curve.GetEndPoint(0), location.Curve.GetEndPoint(1), location.Curve.Evaluate(0.5, true) };
            state.FlexiblePoints = (element as FlexPipe)?.Points?.ToList() ?? (element as FlexDuct)?.Points?.ToList();
            return state;
        }

        private static void RequireSamePoint(XYZ before, XYZ after, string field)
        {
            if ((before == null) != (after == null) || (before != null && before.DistanceTo(after) > 1e-5))
                throw new InvalidOperationException("La connexion déplacerait " + field + ".");
        }

        internal static void VerifyConnectionGuard(Document doc, ConnectionGuard guard, bool verifyHistoricalEndpoints = true)
        {
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            foreach (var saved in guard.States)
            {
                var element = doc.GetElement(saved.UniqueId);
                if (element == null || element.GetTypeId() != saved.TypeId)
                    throw new InvalidOperationException("La connexion supprimerait ou remplacerait un composant : " + saved.UniqueId);
                var actual = CaptureNetworkPlacement(element, null);
                RequireSamePoint(saved.Point, actual.Point, "l’insertion de " + saved.UniqueId);
                RequireSamePoint(saved.Origin, actual.Origin, "l’origine du repère de " + saved.UniqueId);
                RequireSamePoint(saved.BasisX, actual.BasisX, "l’orientation X de " + saved.UniqueId);
                RequireSamePoint(saved.BasisY, actual.BasisY, "l’orientation Y de " + saved.UniqueId);
                RequireSamePoint(saved.BasisZ, actual.BasisZ, "l’orientation Z de " + saved.UniqueId);
                if (saved.Mirrored != actual.Mirrored || saved.HandFlipped != actual.HandFlipped || saved.FacingFlipped != actual.FacingFlipped)
                    throw new InvalidOperationException("La connexion inverserait un composant : " + saved.UniqueId);
                VerifyPointList(saved.CurvePoints, actual.CurvePoints, "l’axe de " + saved.UniqueId);
                VerifyPointList(saved.FlexiblePoints, actual.FlexiblePoints, "le tracé flexible de " + saved.UniqueId);
                if (saved.Ports.Count != actual.Ports.Count || saved.Ports.Any(port => Match(element, port) == null))
                    throw new InvalidOperationException("La connexion modifierait les ports d’un composant : " + saved.UniqueId);
                foreach (var port in saved.Ports)
                {
                    var connector = Match(element, port);
                    VerifyPortSemantics(connector, port, !saved.AllowUndefinedSystemNormalization);
                }
                if (!saved.AllowUndefinedSystemNormalization && saved.SystemType != null && saved.SystemType != actual.SystemType)
                    throw new InvalidOperationException("La connexion changerait le type de système de " + saved.UniqueId);
            }
            foreach (var endpoint in guard.HistoricalEndpoints)
            {
                var connector = Match(doc.GetElement(endpoint.Item1), endpoint.Item2);
                if (connector == null) throw new InvalidOperationException("Connecteur historique déplacé après connexion : " + endpoint.Item1);
                // Revit may assign the restored endpoint’s system only when its transaction commits.
                // Its historical position is checked above in both phases; final semantics remain strict.
                if (verifyHistoricalEndpoints) VerifyPortSemantics(connector, endpoint.Item2, true);
            }
            foreach (var edge in guard.ExistingConnections)
            {
                var a = Match(doc.GetElement(edge.SourceMember), edge.Port);
                var b = Match(doc.GetElement(edge.Peer), edge.PeerPort);
                if (a == null || b == null || !a.IsConnectedTo(b))
                    throw new InvalidOperationException("L’opération supprimerait une connexion physique existante : "
                        + edge.SourceMember + " → " + edge.Peer);
            }
        }

        private static void VerifyPointList(List<XYZ> before, List<XYZ> after, string field)
        {
            if ((before == null) != (after == null) || (before != null && before.Count != after.Count))
                throw new InvalidOperationException("La connexion modifierait " + field + ".");
            if (before != null) for (int i = 0; i < before.Count; i++) RequireSamePoint(before[i], after[i], field);
        }

        private static void VerifyPortSemantics(Connector connector, HistoryPort saved, bool preserveDefinedSystemType)
        {
            if (connector == null) throw new InvalidOperationException("Connecteur absent pendant la validation du réseau.");
            var classification = ReadPortClassification(connector);
            if (IsConcreteClassification(saved.Classification) && classification != saved.Classification)
                throw new InvalidOperationException("La connexion changerait la classification d’un port : " + saved.Classification + " → " + classification);
            if (preserveDefinedSystemType && saved.SystemType != null && !HistoricalSystemTypeMatches(ReadPortSystemType(connector), saved.SystemType, saved.SystemVersion))
                throw new InvalidOperationException("La connexion changerait le type de système historique du port : " + saved.SystemType);
        }

        internal static bool RestoreUndefinedSystemType(Document doc, Element element, HistoryRecipe recipe, out string failure)
        {
            failure = null;
            if ((recipe?.Native ?? recipe?.OpeningFallback)?.SourceSystemTypeDefined != false
                || !ElementHistoryNativeArchive.HasMepSystemType(element)) return true;
            if (ElementHistoryNativeArchive.ReadMepSystemType(element) == null) return true;
            bool piping = element is Pipe || element is FlexPipe;
            var parameter = element.get_Parameter(piping ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
                : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
            {
                failure = "Le type de système source était indéfini, mais le paramètre restauré ne permet pas de rétablir cet état.";
                return false;
            }
            var guard = CaptureConnectionGuard(doc, element, null, recipe);
            using (var attempt = new SubTransaction(doc))
            {
                attempt.Start();
                try
                {
                    if (!parameter.Set(ElementId.InvalidElementId))
                        throw new InvalidOperationException("Revit a refusé le type de système source indéfini.");
                    doc.Regenerate();
                    if (ElementHistoryNativeArchive.ReadMepSystemType(element) != null)
                        throw new InvalidOperationException("Revit conserve un système défini malgré le paramètre indéfini.");
                    VerifyConnectionGuard(doc, guard);
                    if (attempt.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Le rétablissement du système indéfini n’a pas été validé.");
                    return true;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex) { failure = ex.Message; return false; }
                finally { if (attempt.GetStatus() == TransactionStatus.Started) attempt.RollBack(); }
            }
        }
    }
}

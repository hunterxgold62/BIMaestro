using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryRestoration
    {
        internal static HistoryRestoreBatch RestoreNativeCopies(Document destination, IEnumerable<HistoryRestoreRequest> requests)
            => RestoreNativeCopiesCore(destination, requests, false);

        private static HistoryRestoreBatch RestoreNativeCopiesCore(Document destination, IEnumerable<HistoryRestoreRequest> requests, bool allowPartial)
        {
            if (destination == null || destination.IsReadOnly || destination.IsModifiable)
                throw new InvalidOperationException("Le copier-coller natif exige une maquette modifiable hors transaction.");
            var batch = new HistoryRestoreBatch();
            var index = ReadIndex(destination);
            using var sources = new ElementHistoryNativeArchive.Sources();
            // Source Element wrappers can be invalidated by CopyNativeSet rolling
            // back its temporary provenance transaction. Keep immutable identities.
            var selected = new List<Tuple<HistoryRestoreRequest, Document, string>>();
            var validatedCapsules = new HashSet<Tuple<Document,string>>();
            foreach (var request in requests)
            {
                var stage = "ouverture de la copie";
                try
                {
                var artifact = request.Recipe?.Native;
                if (artifact == null) throw new InvalidOperationException("Copie native absente.");
                var source = sources.Open(destination, artifact);
                stage = "identification de l’objet dans la copie";
                var original = artifact.WholeModel
                    ? source.GetElement(ElementIdExtensions.CreateElementId(artifact.SourceElementId))
                    : source.GetElement(artifact.RootUniqueId);
                if (!artifact.WholeModel && original != null)
                {
                    if (validatedCapsules.Add(Tuple.Create(source,artifact.TypeVersion)))
                        ElementHistoryNativeArchive.ValidateDurableTypeCompatibility(source, destination, artifact.TypeVersion);
                    original = ElementHistoryNativeArchive.ResolveNativeMember(source,original,artifact.MemberSourceUniqueId);
                }
                if (original == null || !string.IsNullOrWhiteSpace(request.SourceUniqueId) && artifact.WholeModel && original.UniqueId != request.SourceUniqueId)
                    throw new InvalidOperationException("L’identité de l’objet ne correspond pas à la copie native.");
                if (artifact.ExcludedElementIds?.Contains(original.Id.GetIdValue()) == true)
                    throw new InvalidOperationException("L’objet a été modifié après la copie : cet état ne peut pas être collé.");
                if (artifact.ExcludedElementIds?.Contains(ElementHistoryNativeArchive.OpeningRoot(original).Id.GetIdValue()) == true)
                    throw new InvalidOperationException("Le support a été modifié après la copie : cet état ne peut pas être collé.");
                if (original.ViewSpecific || original is Sketch || original is SketchPlane || original is Dimension
                    || original is IndependentTag || original is MEPSystem || original.Category?.CategoryType == CategoryType.Annotation
                    || original is Autodesk.Revit.DB.Plumbing.PipeInsulation || original is Autodesk.Revit.DB.Mechanical.DuctInsulation) continue;
                ElementHistoryNativeArchive.RequireRestorableNativeRoot(source, original);
                if (artifact.WholeModel)
                {
                    stage = "lecture des connexions et relations natives";
                    var root = ElementHistoryNativeArchive.OpeningRoot(original);
                    artifact.RootSourceUniqueId = root.UniqueId; artifact.RootUniqueId = root.UniqueId;
                    artifact.MemberSourceUniqueId = original.UniqueId;
                    request.Recipe = ElementHistoryNativeArchive.ReadNativeMetadata(original, artifact);
                    request.SourceUniqueId = original.UniqueId;
                }
                selected.Add(Tuple.Create(request, source, original.UniqueId));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Copie native — " + stage + " — " + request.Label + " : " + ex.Message, ex);
                }
            }
            // A capsule containing hosted families also carries native copies of their walls.
            // Reuse that same proved host state for a separately archived wall, so Revit
            // pastes the host and its families together rather than creating a second wall.
            var hostSources = selected.GroupBy(item => item.Item2).OrderByDescending(items => items.Count()).ToList();
            var hostCandidates = new Dictionary<Document, Dictionary<string, List<Element>>>();
            for (int position = 0; position < selected.Count; position++)
            {
                var item = selected[position];
                var original = item.Item2.GetElement(item.Item3);
                var version = item.Item1.Recipe.Native.TypeVersion;
                if (!(original is Wall) || item.Item1.Recipe.Native.WholeModel
                    || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(item.Item1.SourceUniqueId)) continue;
                var originalType = item.Item2.GetElement(original.GetTypeId());
                var definition = ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(originalType);
                if (string.IsNullOrWhiteSpace(definition)) continue;
                foreach (var possible in hostSources)
                {
                    if (ReferenceEquals(possible.Key, item.Item2)
                        || !possible.Any(entry => entry.Item1.Recipe.Native.TypeVersion == version)) continue;
                    if (!hostCandidates.TryGetValue(possible.Key, out var candidates))
                    {
                        candidates = new FilteredElementCollector(possible.Key).WhereElementIsNotElementType()
                            .Where(element => element is HostObject)
                            .Select(element => new { Element = element, Historical = ElementHistoryNativeArchive.NativeHistoricalIdentity(element) })
                            .Where(entry => !string.IsNullOrWhiteSpace(entry.Historical))
                            .GroupBy(entry => entry.Historical, StringComparer.Ordinal)
                            .ToDictionary(entries => entries.Key, entries => entries.Select(entry => entry.Element).ToList(), StringComparer.Ordinal);
                        hostCandidates[possible.Key] = candidates;
                    }
                    if (!candidates.TryGetValue(item.Item1.SourceUniqueId, out var matches) || matches.Count != 1) continue;
                    var candidate = matches[0];
                    var candidateType = possible.Key.GetElement(candidate.GetTypeId());
                    if (candidate.GetType() != original.GetType()
                        || ElementHistoryNativeArchive.ReadHistoricalTypeVersion(candidateType) != version
                        || ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(candidateType) != definition
                        || ElementHistoryNativeArchive.NativeHistoricalIdentity(ElementHistoryNativeArchive.OpeningRoot(candidate)) != item.Item1.Recipe.Native.RootSourceUniqueId
                        || !CohesiveWallStateMatches((Wall)original, (Wall)candidate)
                        || !ElementHistoryReconstruction.VerifyHistoricalPlacement(candidate, item.Item1.Recipe, out _, includeBoundingBox: false)) continue;
                    selected[position] = Tuple.Create(item.Item1, possible.Key, candidate.UniqueId);
                    break;
                }
            }
            var availability = new NativeRestoreAvailability { AllowPartial = allowPartial, Batch = batch };
            foreach (var item in selected)
                foreach (var identity in new[] { item.Item1.SourceUniqueId, item.Item1.Recipe.Native.MemberSourceUniqueId, item.Item3 })
                    if (!string.IsNullOrWhiteSpace(identity)) availability.Selected.Add(identity);
            using var group = new TransactionGroup(destination, "BIMaestro - Coller les objets historiques natifs");
            group.Start();
            var mappings = new List<Tuple<Document, Dictionary<string, Element>>>();
            var mappingTargets = new List<Tuple<Document, Dictionary<string, string>>>();
            var nativeStage = "préparation du collage";
            void RefreshNativeMappings()
            {
                mappings.Clear();
            foreach (var savedMapping in mappingTargets)
            {
                var currentMapping = new Dictionary<string, Element>(StringComparer.Ordinal);
                foreach (var saved in savedMapping.Item2)
                {
                    nativeStage = "relecture après collage : " + saved.Key;
                    var current = destination.GetElement(saved.Value);
                    if (current?.IsValidObject != true)
                    {
                        var original = savedMapping.Item1.GetElement(saved.Key);
                        var historical = original == null ? null : ElementHistoryNativeArchive.NativeHistoricalIdentity(original);
                        var currentUid = Resolve(destination, index, historical) ?? Resolve(destination, index, saved.Key);
                        current = currentUid == null ? null : destination.GetElement(currentUid);
                    }
                    if (current?.IsValidObject != true)
                        throw new InvalidOperationException("Un objet copié n’est plus présent après le collage des archives : " + saved.Key);
                    currentMapping[saved.Key] = current;
                }
                mappings.Add(Tuple.Create(savedMapping.Item1, currentMapping));
            }
            }
            try
            {
                // Revit validates MEP system compatibility at Commit. Keep all
                // native source sets and their boundary connections in the same
                // transaction so no temporarily disconnected lot is committed.
                using (var tx = new Transaction(destination, "BIMaestro - Copier-coller natif historique"))
                {
                    tx.Start();
                    var failures = new RestoreFailures();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    // Restore native hosts before a different capsule pastes their hosted families.
                    foreach (var sourceGroup in selected.GroupBy(item => item.Item2)
                        .OrderByDescending(items => items.Any(item => items.Key.GetElement(item.Item3) is HostObject)))
                    {
                        var source = sourceGroup.Key;
                        nativeStage = "source " + source.PathName;
                        var mapping = new Dictionary<string, Element>(StringComparer.Ordinal);
                        var missing = new HashSet<string>(StringComparer.Ordinal);
                        var replace = new List<ElementId>();
                        foreach (var item in sourceGroup)
                        {
                            var originalUid = item.Item3;
                            var original = source.GetElement(originalUid);
                            if (original?.IsValidObject != true) throw new InvalidOperationException("Objet source natif introuvable avant collage : " + originalUid);
                            var identity = string.IsNullOrWhiteSpace(item.Item1.SourceUniqueId) ? originalUid : item.Item1.SourceUniqueId;
                            var uid = Resolve(destination, index, identity);
                            if (uid != null && uid != identity && GetOrigins(destination.GetElement(uid))?.Contains(identity) == true)
                            {
                                var current = destination.GetElement(uid);
                                var placement = item.Item1.Recipe.HistoricalPlacement == null
                                    ? ElementHistoryReconstruction.CaptureDirectForRestore(original) : item.Item1.Recipe;
                                if (!ElementHistoryNativeArchive.NativeGeometryMatches(original, current)
                                    || placement == null || !ElementHistoryReconstruction.VerifyHistoricalPlacement(current, placement, out _))
                                    throw new InvalidOperationException("Un objet déjà restauré a été modifié : remplacement natif annulé pour " + item.Item1.Label);
                                replace.Add(current.Id); missing.Add(originalUid);
                            }
                            else if (uid != null) mapping[originalUid] = destination.GetElement(uid);
                            else missing.Add(originalUid);
                        }
                        if (missing.Count > 0)
                        {
                            if (replace.Count > 0)
                            {
                                var allowed = new HashSet<ElementId>(replace);
                                var before = new FilteredElementCollector(destination).WhereElementIsNotElementType()
                                    .Where(e => e.Category?.CategoryType == CategoryType.Model && !e.ViewSpecific)
                                    .ToDictionary(e => e.Id, e => e.UniqueId);
                                nativeStage = "remplacement des objets : " + string.Join(", ", replace);
                                var deleted = destination.Delete(replace);
                                if (deleted.Any(id => before.ContainsKey(id) && !allowed.Contains(id)))
                                    throw new InvalidOperationException("Le remplacement supprimerait un autre élément du projet : collage annulé.");
                            }
                            nativeStage = "copie native : " + source.PathName;
                            foreach (var pair in ElementHistoryNativeArchive.CopyNativeSet(source, missing.Select(source.GetElement), destination,
                                sourceGroup.First().Item1.Recipe.Native.WholeModel == false)) mapping[pair.Key] = pair.Value;
                            foreach (var pair in mapping)
                            {
                                nativeStage = "indexation après copie : " + pair.Key + "; valide=" + pair.Value.IsValidObject;
                                foreach (var origin in GetOrigins(pair.Value) ?? new List<string>()) index[origin] = pair.Value.UniqueId;
                            }
                        }
                        // Another capsule can regenerate a previously copied wall or its hosted objects.
                        // Save identities while these wrappers are valid; re-read them after all copies.
                        mappingTargets.Add(Tuple.Create(source, mapping.ToDictionary(pair => pair.Key, pair => pair.Value.UniqueId, StringComparer.Ordinal)));
                        foreach (var item in sourceGroup)
                        {
                            var originalUid = item.Item3;
                            var original = source.GetElement(originalUid);
                            if (original?.IsValidObject != true) throw new InvalidOperationException("Objet source natif introuvable après collage : " + originalUid);
                            nativeStage = "bilan après copie : " + originalUid;
                            var copied = mapping[originalUid];
                            var existed = !missing.Contains(originalUid);
                            batch.Items.Add(new HistoryRestoreItem { Label = item.Item1.Label, SourceUniqueId = item.Item1.SourceUniqueId ?? originalUid,
                                Category = original.Category?.Name, RecipeKind = "native_copy", UniqueId = copied.UniqueId,
                                Created = !existed, Existing = existed, DeletionAudit = item.Item1.DeletionAudit });
                            index[originalUid] = copied.UniqueId;
                        }
                    }
                    RefreshNativeMappings();
                    foreach (var entry in mappings.SelectMany(m => m.Item2))
                    {
                        nativeStage = "indexation globale : " + entry.Key + "; valide=" + entry.Value.IsValidObject;
                        index[entry.Key] = entry.Value.UniqueId;
                        var historical = ElementHistoryNativeArchive.NativeHistoricalIdentity(entry.Value);
                        if (!string.IsNullOrWhiteSpace(historical)) index[historical] = entry.Value.UniqueId;
                    }
                    foreach (var item in batch.Items)
                    {
                        var currentUid = Resolve(destination, index, item.SourceUniqueId);
                        if (currentUid == null) throw new InvalidOperationException("Objet restauré absent après regroupement : " + item.SourceUniqueId);
                        item.UniqueId = currentUid;
                        index[item.SourceUniqueId] = currentUid;
                    }
                    nativeStage = "raccordement des archives";
                    foreach (var item in mappings) ConnectNativeBoundary(destination, item.Item1, item.Item2, index, availability);
                    nativeStage = "raccordement historique";
                    ConnectSavedNativeBoundary(destination, selected.Select(i => i.Item1), index, availability);
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Copier-coller natif et liaisons refusés par Revit. " + failures.Message);
                    }
                nativeStage = "relations et placement après validation";
                ElementHistoryRelations.Restore(destination, selected.Select(i => i.Item1).ToList(), index, batch);
                VerifyFinalPlacements(destination, selected.Select(i => i.Item1).ToList(), batch);
                // In best-effort mode a failed wall join must not cancel other native objects.
                // Intrinsic placement (axis, origin, orientation, level) remains strict.
                var criticalPlacements = allowPartial ? batch.RepairFailures.Where(message =>
                    !message.Contains("L’encombrement historique de l’objet n’a pas été rétabli.")).ToList() : batch.RepairFailures;
                if (criticalPlacements.Count > 0 || !allowPartial && batch.RelationFailures.Count > 0)
                    throw new InvalidOperationException("Copie native annulée : les positions ou relations historiques ne sont pas intégralement rétablies. "
                        + string.Join("\n", criticalPlacements.Concat(batch.RelationFailures).Take(3)));
                nativeStage = "vérification des réseaux et supports";
                RefreshNativeMappings(); // Wall joins can regenerate the copied native wrappers again.
                var savedSystemTargets = new Dictionary<string,string>(StringComparer.Ordinal);
                var savedSystemSources = new Dictionary<string,string>(StringComparer.Ordinal);
                foreach (var item in mappings)
                {
                    batch.ConnectionsExisting += ElementHistoryNativeArchive.VerifyNativeSetConnections(item.Item1, item.Item2, destination);
                    ElementHistoryNativeArchive.VerifyNativeHosts(item.Item1, item.Item2, destination);
                    var savedRequests = selected.Where(entry => ReferenceEquals(entry.Item2,item.Item1)).Select(entry => entry.Item1);
                    if (!VerifySavedNativeSystems(destination,savedRequests,index,item.Item1,item.Item2,savedSystemTargets,savedSystemSources,availability))
                        VerifyNativeSystemsCore(item.Item1, item.Item2, destination, availability);
                }
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Le copier-coller natif n’a pas pu être validé.");
                return batch;
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") == "1")
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-restore-batch-before-rollback.json"),
                        Newtonsoft.Json.JsonConvert.SerializeObject(new { Stage = nativeStage, Batch = batch }, Newtonsoft.Json.Formatting.Indented)); } catch { }
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw new InvalidOperationException("Collage natif — " + nativeStage + " : " + ex.Message, ex);
            }
        }

        private static bool CohesiveWallStateMatches(Wall baseline, Wall candidate)
        {
            if (baseline.Flipped != candidate.Flipped) return false;
            bool ReferenceMatches(Element first, Element second)
            {
                if (first == null || second == null) return first == null && second == null;
                var declared = ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(first);
                if (!string.IsNullOrWhiteSpace(declared))
                    return declared == ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(second);
                bool IsIdentity(string value) => value != null && value.Length > 37 && value[36] == '-'
                    && Guid.TryParse(value.Substring(0, 36), out _);
                var identities = new HashSet<string>((GetOrigins(first) ?? new List<string>()).Concat(new[] { first.UniqueId }).Where(IsIdentity));
                return (GetOrigins(second) ?? new List<string>()).Concat(new[] { second.UniqueId }).Where(IsIdentity).Any(identities.Contains);
            }
            foreach (var name in new[] { "WALL_BASE_CONSTRAINT", "WALL_BASE_OFFSET", "WALL_HEIGHT_TYPE",
                "WALL_TOP_OFFSET", "WALL_USER_HEIGHT_PARAM", "WALL_KEY_REF_PARAM", "WALL_STRUCTURAL_SIGNIFICANT",
                "WALL_BOTTOM_IS_ATTACHED", "WALL_TOP_IS_ATTACHED", "WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL" })
            {
                if (!Enum.TryParse(name, out BuiltInParameter builtin)) continue;
                var first = baseline.get_Parameter(builtin); var second = candidate.get_Parameter(builtin);
                if (first == null || second == null) { if (first != null || second != null) return false; continue; }
                if (first.StorageType != second.StorageType || first.HasValue != second.HasValue) return false;
                if (!first.HasValue) continue;
                switch (first.StorageType)
                {
                    case StorageType.Double:
                        if (double.IsNaN(first.AsDouble()) || double.IsInfinity(first.AsDouble()) || double.IsNaN(second.AsDouble()) || double.IsInfinity(second.AsDouble())
                            || Math.Abs(first.AsDouble() - second.AsDouble()) > 1e-7) return false; break;
                    case StorageType.Integer: if (first.AsInteger() != second.AsInteger()) return false; break;
                    case StorageType.String: if (!string.Equals(first.AsString(), second.AsString(), StringComparison.Ordinal)) return false; break;
                    case StorageType.ElementId:
                        var a = first.AsElementId(); var b = second.AsElementId();
                        if (a.GetIdLongValue() < 0 || b.GetIdLongValue() < 0) { if (a.GetIdLongValue() != b.GetIdLongValue()) return false; }
                        else if (!ReferenceMatches(baseline.Document.GetElement(a), candidate.Document.GetElement(b))) return false;
                        break;
                }
            }
            var profile = baseline.Document.GetElement(baseline.SketchId) as Sketch;
            var otherProfile = candidate.Document.GetElement(candidate.SketchId) as Sketch;
            if (profile == null || otherProfile == null) return profile == null && otherProfile == null;
            var curves = profile.Profile.Cast<CurveArray>().SelectMany(loop => loop.Cast<Curve>()).ToList();
            var others = otherProfile.Profile.Cast<CurveArray>().SelectMany(loop => loop.Cast<Curve>()).ToList();
            if (curves.Count != others.Count) return false;
            foreach (var curve in curves)
            {
                var match = others.FirstOrDefault(other => other.GetType() == curve.GetType()
                    && Math.Abs(other.Length - curve.Length) < 1e-6
                    && other.Evaluate(0.5, true).DistanceTo(curve.Evaluate(0.5, true)) < 1e-5
                    && ((other.GetEndPoint(0).DistanceTo(curve.GetEndPoint(0)) < 1e-5 && other.GetEndPoint(1).DistanceTo(curve.GetEndPoint(1)) < 1e-5)
                        || (other.GetEndPoint(0).DistanceTo(curve.GetEndPoint(1)) < 1e-5 && other.GetEndPoint(1).DistanceTo(curve.GetEndPoint(0)) < 1e-5)));
                if (match == null) return false; others.Remove(match);
            }
            return true;
        }

        private sealed class NativeRestoreAvailability
        {
            internal bool AllowPartial;
            internal HistoryRestoreBatch Batch;
            internal readonly HashSet<string> Selected = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> MissingSystems = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> MissingPeers = new HashSet<string>(StringComparer.Ordinal);
            internal bool SkipMissingPeer(string identity, IEnumerable<string> aliases, HistoryPort port, HistoryPort peerPort)
            {
                if (!AllowPartial || string.IsNullOrWhiteSpace(identity) || Selected.Contains(identity)
                    || (aliases ?? Enumerable.Empty<string>()).Any(Selected.Contains)) return false;
                MissingPeers.Add(identity);
                foreach (var saved in new[] { port, peerPort })
                    if (!string.IsNullOrWhiteSpace(saved?.SystemSourceUniqueId)) MissingSystems.Add(saved.SystemSourceUniqueId);
                var message = "Connexion non rétablie : voisin historique non sélectionné ou non récupérable : " + identity;
                if (!Batch.ConnectionFailures.Contains(message)) Batch.ConnectionFailures.Add(message);
                return true;
            }
            internal void SystemDifference(string identity, string message)
            {
                if (!AllowPartial) throw new InvalidOperationException(message);
                var item = Batch.Items.FirstOrDefault(value => value.SourceUniqueId == identity);
                var warning = (item?.Label ?? identity) + " : " + message;
                if (!Batch.CaptureWarnings.Contains(warning)) Batch.CaptureWarnings.Add(warning);
                if (item != null && (item.Detail == null || !item.Detail.Contains(message)))
                    item.Detail = string.IsNullOrWhiteSpace(item.Detail) ? message : item.Detail + " " + message;
            }

            internal bool SystemGap(string identity, string message)
            {
                if (!AllowPartial || string.IsNullOrWhiteSpace(identity) || !MissingSystems.Contains(identity)) return false;
                var warning = message + " Système historique : " + identity + "; voisins absents : " + string.Join(", ", MissingPeers);
                if (!Batch.CaptureWarnings.Contains(warning)) Batch.CaptureWarnings.Add(warning);
                return true;
            }
        }

        private static void ConnectNativeBoundary(Document destination, Document source, Dictionary<string, Element> mapping,
            Dictionary<string, string> index, NativeRestoreAvailability availability)
        {
            foreach (var pair in mapping.ToList())
            foreach (var link in ElementHistoryNetwork.CaptureConnections(source.GetElement(pair.Key), new List<string>()))
            {
                if (mapping.ContainsKey(link.Peer)) continue;
                var peerUid = Resolve(destination, index, link.Peer);
                if (peerUid == null)
                {
                    var historicalPeer = source.GetElement(link.Peer);
                    peerUid = (historicalPeer == null ? new List<string>() : GetOrigins(historicalPeer) ?? new List<string>()).Select(origin => Resolve(destination, index, origin))
                        .FirstOrDefault(uid => uid != null);
                }
                if (peerUid == null)
                {
                    var historicalPeer = source.GetElement(link.Peer);
                    var aliases = historicalPeer == null ? null : GetOrigins(historicalPeer);
                    if (availability.SkipMissingPeer(link.Peer, aliases, link.Port, link.PeerPort)) continue;
                    throw new InvalidOperationException("Copie native annulée : voisin historique sélectionné absent : " + link.Peer);
                }
                var peer = destination.GetElement(peerUid);
                var a = ElementHistoryNetwork.Match(pair.Value, link.Port); var b = ElementHistoryNetwork.Match(peer, link.PeerPort);
                if (a == null || b == null || a.Origin.DistanceTo(b.Origin) > 1e-5)
                    throw new InvalidOperationException("Copie native annulée : connecteurs historiques non coïncidents.");
                if (a.IsConnectedTo(b)) continue;
                if (a.IsConnected || b.IsConnected) throw new InvalidOperationException("Copie native annulée : connecteur utilisé par une autre liaison.");
                var guard = ElementHistoryNetwork.CaptureConnectionGuard(destination, pair.Value, peer);
                a.ConnectTo(b); destination.Regenerate();
                ElementHistoryNetwork.VerifyConnectionGuard(destination, guard);
                if (!a.IsConnectedTo(b)) throw new InvalidOperationException("La connexion native au réseau conservé n’a pas été rétablie.");
            }
        }

        // Partial capsules can split a logical system internally. Validate the frozen
        // original identities, not those temporary capsule system IDs.
        private static bool VerifySavedNativeSystems(Document destination, IEnumerable<HistoryRestoreRequest> requests,
            Dictionary<string,string> index, Document source, Dictionary<string,Element> mapping,
            Dictionary<string,string> systems, Dictionary<string,string> sourceSystems, NativeRestoreAvailability availability)
        {
            var savedRequests = requests.ToList();
            // A raw whole-model source already retains the original logical partition.
            if (savedRequests.All(request=>request.Recipe?.Native?.WholeModel==true
                && request.Recipe.Native.PreserveLogicalSystemIdentities!=true)) return false;
            var records = new List<Tuple<string,HistoryPort>>();
            void Add(string owner, HistoryPort port)
            {
                if (port == null || port.Domain != (int)Domain.DomainPiping && port.Domain != (int)Domain.DomainHvac) return;
                records.Add(Tuple.Create(owner,port));
            }
            foreach (var request in savedRequests)
            {
                var owner = request.SourceUniqueId ?? request.Recipe?.Native?.MemberSourceUniqueId;
                foreach (var port in request.Recipe?.Ports ?? new List<HistoryPort>()) Add(owner,port);
                foreach (var edge in request.Recipe?.Connections ?? new List<HistoryConnection>())
                {
                    Add(edge.SourceMember ?? owner,edge.Port);
                    Add(edge.Peer,edge.PeerPort);
                }
            }
            if (!savedRequests.Any(request=>request.Recipe?.Native?.PreserveLogicalSystemIdentities==true)
                && !records.Any(record=>record.Item2.SystemSourceCaptured==true || !string.IsNullOrWhiteSpace(record.Item2.SystemSourceUniqueId))) return false;
            var covered = new Dictionary<string,Dictionary<int,string>>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var saved = record.Item2;
                if (saved.SystemSourceCaptured!=true && string.IsNullOrWhiteSpace(saved.SystemSourceUniqueId)) continue;
                var ownerUid = string.IsNullOrWhiteSpace(record.Item1) ? null : Resolve(destination,index,record.Item1);
                var owner = ownerUid == null ? null : destination.GetElement(ownerUid);
                if (owner == null && availability.SkipMissingPeer(record.Item1, null, saved, null)) continue;
                var port = ElementHistoryNetwork.Match(owner,saved);
                if (port == null)
                    throw new InvalidOperationException("Copie native annulée : connecteur historique du système absent ou modifié : "+record.Item1);
                var actualSystem = port.MEPSystem;
                if (actualSystem == null && !string.IsNullOrWhiteSpace(saved.SystemSourceUniqueId)
                    && !availability.SystemGap(saved.SystemSourceUniqueId, "Système logique non réattribué sur les éléments récupérés."))
                    availability.SystemDifference(record.Item1, "Système historique non réattribué après collage : " + saved.SystemSourceUniqueId);
                var actualType = actualSystem == null ? ElementHistoryNativeArchive.ReadMepSystemType(owner) as MEPSystemType
                    : destination.GetElement(actualSystem.GetTypeId()) as MEPSystemType;
                var classification = port.Domain == Domain.DomainPiping ? port.PipeSystemType.ToString() : port.DuctSystemType.ToString();
                var unassignedPartial = actualSystem == null && actualType == null && IsUndefinedNativePortClassification(port)
                    && availability.SystemGap(saved.SystemSourceUniqueId, "Le connecteur reste sans type ni système logique : son ancre historique est absente de la restauration partielle.");
                if (!unassignedPartial && !string.IsNullOrWhiteSpace(saved.Classification) && saved.Classification != classification)
                    availability.SystemDifference(record.Item1, "Classification du connecteur à vérifier : " + saved.Classification + " → " + classification
                        + "; port=" + port.Id + "; système attendu=" + (saved.SystemSourceUniqueId ?? "aucun enregistré")
                        + "; système obtenu=" + (actualSystem?.UniqueId ?? "aucun") + ".");
                // Canonical types are reused only after CopyNativeSet's proven native type comparison;
                // imported frozen types retain both their source UID and definition version.
                if (!unassignedPartial && !string.IsNullOrWhiteSpace(saved.SystemType) && (actualType == null
                    || actualType.UniqueId != saved.SystemType
                    && !ElementHistoryNetwork.HistoricalSystemTypeMatches(actualType,saved.SystemType,saved.SystemVersion)))
                    availability.SystemDifference(record.Item1, "Type de système du connecteur à vérifier : attendu=" + saved.SystemType
                        + "; obtenu=" + (actualType?.UniqueId ?? "aucun") + ".");
                // An explicitly captured absence has no logical identity to manufacture.
                // Native Revit may attach that port while physical neighbors are reconnected.
                if (!string.IsNullOrWhiteSpace(saved.SystemSourceUniqueId) && actualSystem != null)
                {
                    if (systems.TryGetValue(saved.SystemSourceUniqueId,out var target) && target != actualSystem.UniqueId
                        && !availability.SystemGap(saved.SystemSourceUniqueId, "Le réseau récupéré est réparti entre plusieurs systèmes logiques après restauration partielle."))
                        availability.SystemDifference(record.Item1, "Le système historique " + saved.SystemSourceUniqueId + " est réparti entre plusieurs systèmes après collage.");
                    if (sourceSystems.TryGetValue(actualSystem.UniqueId,out var original) && original != saved.SystemSourceUniqueId)
                        availability.SystemDifference(record.Item1, "Les systèmes historiques " + original + " et " + saved.SystemSourceUniqueId + " ont été regroupés par Revit après collage.");
                    systems[saved.SystemSourceUniqueId]=actualSystem.UniqueId;
                    sourceSystems[actualSystem.UniqueId]=saved.SystemSourceUniqueId;
                }
                if (!covered.TryGetValue(owner.UniqueId,out var ports)) covered[owner.UniqueId]=ports=new Dictionary<int,string>();
                if (ports.TryGetValue(port.Id,out var previous) && previous != saved.SystemSourceUniqueId)
                    throw new InvalidOperationException("L’identité historique du système est contradictoire sur un connecteur.");
                ports[port.Id]=saved.SystemSourceUniqueId;
            }
            // Do not silently certify a new partial archive whose system metadata is incomplete.
            foreach (var pair in mapping)
            {
                var original = source.GetElement(pair.Key);
                foreach (var sourcePort in ElementHistoryNetwork.CaptureNativeSystemPorts(original))
                {
                    var actual = destination.GetElement(pair.Value.UniqueId);
                    var actualPort = ElementHistoryNetwork.Match(actual,sourcePort);
                    if (actualPort == null || !covered.TryGetValue(actual.UniqueId,out var ports) || !ports.ContainsKey(actualPort.Id))
                        throw new InvalidOperationException("L’identité du système historique n’est pas enregistrée pour tous les connecteurs du lot : "+pair.Key);
                }
            }
            return true;
        }

        private static bool IsUndefinedNativePortClassification(Connector port)
            => port.Domain == Domain.DomainPiping
                ? port.PipeSystemType == Autodesk.Revit.DB.Plumbing.PipeSystemType.UndefinedSystemType
                : port.Domain == Domain.DomainHvac && port.DuctSystemType == Autodesk.Revit.DB.Mechanical.DuctSystemType.UndefinedSystemType;

        internal static void VerifyNativeSystems(Document source, Dictionary<string, Element> mapping, Document destination)
            => VerifyNativeSystemsCore(source, mapping, destination, new NativeRestoreAvailability());

        private static void VerifyNativeSystemsCore(Document source, Dictionary<string, Element> mapping, Document destination,
            NativeRestoreAvailability availability)
        {
            var systems = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceSystems = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in mapping)
            {
                var original = source.GetElement(pair.Key); var actual = destination.GetElement(pair.Value.UniqueId);
                foreach (var originalPort in ElementHistoryNetwork.Ports(original))
                {
                    if (originalPort.Domain != Domain.DomainPiping && originalPort.Domain != Domain.DomainHvac) continue;
                    var expected = originalPort.MEPSystem;
                    if (expected == null) continue;
                    var port = ElementHistoryNetwork.Ports(actual).SingleOrDefault(p => p.Domain == originalPort.Domain
                        && p.Shape == originalPort.Shape && p.Origin.DistanceTo(originalPort.Origin) < 1e-5);
                    if (port == null) throw new InvalidOperationException("Copie native annulée : connecteur MEP absent après collage.");
                    var system = port.MEPSystem;
                    if (system == null)
                    {
                        var expectedWithoutSystem = source.GetElement(expected.GetTypeId()) as MEPSystemType;
                        var availableType = ElementHistoryNativeArchive.ReadMepSystemType(actual) as MEPSystemType;
                        var unassigned = availableType == null && IsUndefinedNativePortClassification(port);
                        if (!unassigned && expectedWithoutSystem?.SystemClassification != availableType?.SystemClassification)
                            throw new InvalidOperationException("Copie native annulée : classification du système différente de la source.");
                        if (availability.SystemGap(expected.UniqueId, "Système logique non réattribué sur les éléments récupérés.")) continue;
                        throw new InvalidOperationException("Copie native annulée : système MEP absent après collage.");
                    }
                    if (systems.TryGetValue(expected.UniqueId, out var existing) && existing != system.UniqueId
                        && !availability.SystemGap(expected.UniqueId, "Le réseau récupéré est réparti entre plusieurs systèmes logiques après restauration partielle."))
                        throw new InvalidOperationException("Copie native annulée : un réseau historique a été divisé en plusieurs systèmes.");
                    if (sourceSystems.TryGetValue(system.UniqueId, out var priorSource) && priorSource != expected.UniqueId)
                        throw new InvalidOperationException("Copie native annulée : deux systèmes historiques distincts ont été fusionnés.");
                    var expectedType = source.GetElement(expected.GetTypeId()) as MEPSystemType;
                    var actualType = destination.GetElement(system.GetTypeId()) as MEPSystemType;
                    if (expectedType?.SystemClassification != actualType?.SystemClassification)
                        throw new InvalidOperationException("Copie native annulée : classification du système différente de la source.");
                    systems[expected.UniqueId] = system.UniqueId;
                    sourceSystems[system.UniqueId] = expected.UniqueId;
                }
            }
        }
        private static void ConnectSavedNativeBoundary(Document destination, IEnumerable<HistoryRestoreRequest> requests,
            Dictionary<string, string> index, NativeRestoreAvailability availability)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in requests.Where(r => r.Recipe.Native.WholeModel == false))
            foreach (var edge in request.Recipe.Connections ?? new List<HistoryConnection>())
            {
                var sourceUid = Resolve(destination, index, edge.SourceMember ?? request.SourceUniqueId);
                var peerUid = Resolve(destination, index, edge.Peer);
                var sourceIdentity = edge.SourceMember ?? request.SourceUniqueId;
                if (sourceUid == null)
                {
                    if (availability.SkipMissingPeer(sourceIdentity, null, edge.Port, edge.PeerPort)) continue;
                    throw new InvalidOperationException("Copie native annulée : membre historique sélectionné absent : " + sourceIdentity);
                }
                if (peerUid == null)
                {
                    if (availability.SkipMissingPeer(edge.Peer, null, edge.Port, edge.PeerPort)) continue;
                    throw new InvalidOperationException("Copie native annulée : voisin historique sélectionné absent : " + edge.Peer);
                }
                var a = ElementHistoryNetwork.Match(destination.GetElement(sourceUid), edge.Port);
                var b = ElementHistoryNetwork.Match(destination.GetElement(peerUid), edge.PeerPort);
                if (a == null || b == null) throw new InvalidOperationException("Les connecteurs historiques de la capsule sont introuvables.");
                var key = string.CompareOrdinal(sourceUid, peerUid) < 0 ? sourceUid + ":" + a.Id + "|" + peerUid + ":" + b.Id
                    : peerUid + ":" + b.Id + "|" + sourceUid + ":" + a.Id;
                if (!visited.Add(key) || a.IsConnectedTo(b)) continue;
                if (a.IsConnected || b.IsConnected || a.Origin.DistanceTo(b.Origin) > 1e-5)
                    throw new InvalidOperationException("Copie native annulée : une liaison de la capsule ne peut pas être rétablie exactement.");
                var guard = ElementHistoryNetwork.CaptureConnectionGuard(destination, a.Owner, b.Owner);
                a.ConnectTo(b); destination.Regenerate();
                ElementHistoryNetwork.VerifyConnectionGuard(destination, guard);
                if (!a.IsConnectedTo(b)) throw new InvalidOperationException("Connexion native de la capsule non conservée.");
            }
        }
    }
}

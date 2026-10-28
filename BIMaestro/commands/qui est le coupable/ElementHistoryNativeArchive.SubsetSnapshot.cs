using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        private sealed class NativeSubsetProof
        {
            internal string UniqueId, Host;
            internal List<HistoryConnection> Connections;
            internal List<Tuple<XYZ, Domain, ConnectorProfileType, string>> Systems;
        }

        // The owned historical document becomes the partial archive in memory.
        // Only SaveAs(newPath) is allowed: its original checkpoint on disk is
        // never saved or replaced. The caller must close Sources on every exit.
        private static Dictionary<string, HistoryRecipe> ExportNativeSnapshotSubset(Document source,
            IEnumerable<HistoryRestoreRequest> requests, string outputRvt)
        {
            if (source == null || source.IsFamilyDocument || source.IsReadOnly || source.IsModifiable)
                throw new InvalidOperationException("L’export natif partiel exige un projet historique modifiable hors transaction.");
            if (string.IsNullOrWhiteSpace(outputRvt) || !Path.IsPathRooted(outputRvt)
                || !string.Equals(Path.GetExtension(outputRvt), ".rvt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("La capsule exige un nouveau chemin RVT absolu.", nameof(outputRvt));
            outputRvt = Path.GetFullPath(outputRvt);
            var originalPath = source.PathName;
            var normalizedSource = (originalPath ?? "").Replace('/', '\\');
            var isolatedTest = Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") == "1"
                && normalizedSource.IndexOf("\\tmp\\history-native-service\\cases\\", StringComparison.OrdinalIgnoreCase) >= 0
                && string.Equals(Path.GetFileName(normalizedSource), "opening.rvt", StringComparison.OrdinalIgnoreCase);
            if (!Owns(source) && !isolatedTest)
                throw new InvalidOperationException("L’export partiel ne peut modifier que sa copie historique isolée.");
            if (!string.IsNullOrEmpty(originalPath) && string.Equals(Path.GetFullPath(originalPath), outputRvt, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La capsule ne peut pas remplacer la copie source.");
            if (File.Exists(outputRvt)) throw new IOException("Le fichier de capsule existe déjà.");
            if (source.IsWorkshared && !source.IsDetached)
                throw new InvalidOperationException("Une copie partagée doit être détachée avant tout export natif partiel.");

            var originalVersion = Document.GetDocumentVersion(source).VersionGUID.ToString("N");
            var stage = "resolve_native_subset";
            int keepCount = 0, requestedCount = 0, pruneCount = 0, nonDeletableCount = 0;
            var manual = Environment.GetEnvironmentVariable("BIMAESTRO_HISTORY_MANUAL_TEST") == "1";
            void SetStage(string next)
            {
                stage = next;
                var atUtc = DateTime.UtcNow;
                Trace.WriteLine("Native subset stage=" + stage + "; atUtc=" + atUtc.ToString("O") + "; keep=" + keepCount + "; prune=" + pruneCount);
                if (!manual) return;
                try
                {
                    Directory.CreateDirectory(CollaborativeModelTrackerStore.ActiveDirectory);
                    File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory, "native-subset-progress.json"),
                        Newtonsoft.Json.JsonConvert.SerializeObject(new { Stage=stage, AtUtc=atUtc, Requested=requestedCount,
                            Keep=keepCount, Prune=pruneCount, NonDeletable=nonDeletableCount, Source=originalPath, Output=outputRvt }, Newtonsoft.Json.Formatting.Indented));
                }
                catch (Exception failure) { Trace.WriteLine("Native subset progress: " + failure.Message); }
            }
            var result = new Dictionary<string, HistoryRecipe>(StringComparer.Ordinal);
            void RecordPlacements(string checkpoint)
            {
                if (!manual) return;
                var changed = new List<object>();
                foreach (var pair in result)
                {
                    var element = source.GetElement(pair.Key);
                    if (element == null || ElementHistoryReconstruction.VerifyHistoricalPlacement(element, pair.Value, out _, true)) continue;
                    var actual = new HistoryRecipe();
                    ElementHistoryReconstruction.CaptureHistoricalPlacement(element, actual, true);
                    ElementHistoryReconstruction.VerifyHistoricalPlacement(element,pair.Value,out var reason,true);
                    changed.Add(new { Identity=pair.Key, Class=element.GetType().FullName, Name=element.Name, Reason=reason, Before=pair.Value.HistoricalPlacement, After=actual.HistoricalPlacement, Relations=pair.Value.GeometryRelations });
                }
                File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-subset-placement-"+checkpoint+".json"),
                    Newtonsoft.Json.JsonConvert.SerializeObject(changed,Newtonsoft.Json.Formatting.Indented));
            }
            try
            {
                SetStage("resolve_native_subset");
                var items = (requests ?? Enumerable.Empty<HistoryRestoreRequest>()).Select(request => ResolveDurableItem(source, request))
                    .GroupBy(item => item.Element.UniqueId, StringComparer.Ordinal).Select(group => group.First()).ToList();
                requestedCount = items.Count;
                if (items.Count == 0) return result;
                if (items.Any(item => !IsDocumentModelElement(source, item.Element) || item.Element is MEPSystem))
                    throw new InvalidOperationException("Une dépendance interne ou un système logique ne peut pas être archivé comme un objet autonome.");
                var version = originalVersion;
                var coldPlacement = new Dictionary<string,HistoryRecipe>(StringComparer.Ordinal);
                if (manual) foreach(var item in items)
                {
                    var state=new HistoryRecipe(); ElementHistoryReconstruction.CaptureHistoricalPlacement(item.Element,state,true);
                    coldPlacement[item.Element.UniqueId]=state;
                }
                SetStage("regenerate_owned_historical_source");
                using(var warmup=new Transaction(source,"BIMaestro - Développer la géométrie historique"))
                {
                    warmup.Start(); ConfigureDurableFailures(warmup);
                    // Prepare the native clipboard identities before freezing geometry;
                    // Revit must develop the native family state before it is compared.
                    foreach(var item in items)
                    {
                        var schema=NativeCopySchema();var entity=new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);
                        entity.Set(schema.GetField("CopySourceUniqueId"),item.Element.UniqueId);
                        entity.Set(schema.GetField("HistoricalUniqueId"),item.Element.UniqueId);
                        item.Element.SetEntity(entity);
                    }
                    source.Regenerate(); CommitDurableTransaction(warmup);
                }
                if(manual)
                {
                    var changed=new List<object>();
                    foreach(var item in items)
                    {
                        if(ElementHistoryReconstruction.VerifyHistoricalPlacement(item.Element,coldPlacement[item.Element.UniqueId],out var reason,true))continue;
                        var current=new HistoryRecipe();ElementHistoryReconstruction.CaptureHistoricalPlacement(item.Element,current,true);
                        changed.Add(new {UID=item.Element.UniqueId,Reason=reason,Before=coldPlacement[item.Element.UniqueId].HistoricalPlacement,After=current.HistoricalPlacement});
                    }
                    File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-subset-warmup.json"),Newtonsoft.Json.JsonConvert.SerializeObject(changed,Newtonsoft.Json.Formatting.Indented));
                }
                var keep = new Dictionary<string, Element>(StringComparer.Ordinal);
                var externalContext = new HashSet<ElementId>();
                var pending = new Queue<Element>();
                var expandedRoots = new HashSet<string>(StringComparer.Ordinal);
                var constraintWarnings = new List<string>();
                var constraints = BuildNativeConstraintContext(source,constraintWarnings);
                if(manual) File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-constraint-warnings.json"),Newtonsoft.Json.JsonConvert.SerializeObject(constraintWarnings));
                void Add(Element element)
                {
                    if (element == null || !IsDocumentModelElement(source, element)) return;
                    // An import referenced by a native constraint is context only. It is never a paste input.
                    if(ElementHistoryExternalModels.IsExcluded(element))
                    {
                        externalContext.Add(element.Id);
                        if(element.GetTypeId()!=ElementId.InvalidElementId)externalContext.Add(element.GetTypeId());
                    }
                    var classification = new HistoryOpeningClassification {
                        SourceElementId=element.Id.GetIdValue(), SourceUniqueId=element.UniqueId,
                        Class=element.GetType().FullName, Category=element.Category?.Name,
                        CategoryId=element.Category?.Id.GetIdLongValue()??0, ViewSpecific=element.ViewSpecific,
                        IsDocumentElement=true,
                        HasBoundingBox=element.GetType()==typeof(Element) && element.Category==null && element.get_BoundingBox(null)!=null
                    };
                    // Persisted axes, sketches and bookkeeping dependants are
                    // not protected physical objects. Their owner carries them;
                    // regeneration may legitimately replace their internal IDs.
                    if (classification.IsExcluded()) return;
                    if (keep.ContainsKey(element.UniqueId)) return;
                    keep.Add(element.UniqueId, element); pending.Enqueue(element);
                }
                foreach (var element in ExpandNativeMepContext(source, items.Select(item => item.Element).ToList())) Add(element);
                SetStage("close_native_relations");
                while (pending.Count > 0)
                {
                    var element = pending.Dequeue();
                    if(constraints.TryGetValue(element.Id,out var anchors)) foreach(var id in anchors) Add(source.GetElement(id));
                    var root = OpeningRoot(element); Add(root);
                    if (expandedRoots.Add(root.UniqueId))
                        foreach (var member in Aggregate(source, root)) Add(member);
                    foreach (var relation in ElementHistoryRelations.Capture(element, new List<string>()))
                    {
                        if (!string.IsNullOrWhiteSpace(relation.First)) Add(source.GetElement(relation.First));
                        if (!string.IsNullOrWhiteSpace(relation.Second)) Add(source.GetElement(relation.Second));
                    }
                }
                keepCount = keep.Count;
                SetStage("freeze_native_metadata");
                var keepIdentities = keep.Values.Select(element => new {
                    UniqueId=element.UniqueId, Id=element.Id.GetIdLongValue(), Class=element.GetType().FullName,
                    Category=element.Category?.Name, CategoryId=element.Category?.Id.GetIdLongValue()??0,
                    Name=element.Name, RootUniqueId=OpeningRoot(element).UniqueId
                }).ToDictionary(value=>value.UniqueId,StringComparer.Ordinal);
                if (manual)
                    File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-subset-keep-diagnostic.json"),
                        Newtonsoft.Json.JsonConvert.SerializeObject(new { AtUtc=DateTime.UtcNow, Requested=items.Count, Keep=keepIdentities.Values },Newtonsoft.Json.Formatting.Indented));
                foreach (var item in items)
                {
                    var recipe = Recipe(item.Element, DurableArtifact(item, outputRvt, item.Root.UniqueId, version));
                    PreserveDurableLiveMetadata(item, recipe);
                    recipe = Newtonsoft.Json.Linq.JObject.FromObject(recipe).ToObject<HistoryRecipe>();
                    StampDurablePortVersions(recipe, version);
                    result.Add(item.Element.UniqueId, recipe);
                }
                RecordPlacements("after-freeze");
                var proofs = keep.Values.Select(element => new NativeSubsetProof {
                    UniqueId = element.UniqueId,
                    Host = element is FamilyInstance family ? family.Host?.UniqueId : null,
                    Connections = ElementHistoryNetwork.CaptureConnections(element, new List<string>()),
                    Systems = ElementHistoryNetwork.Ports(element)
                        .Where(port => port.Domain == Domain.DomainPiping || port.Domain == Domain.DomainHvac)
                        .Select(port => Tuple.Create(new XYZ(port.Origin.X, port.Origin.Y, port.Origin.Z), port.Domain, port.Shape, port.MEPSystem?.UniqueId)).ToList()
                }).ToList();
                var definitions = DurableDefinitions(source).ToDictionary(element => element.UniqueId, StringComparer.Ordinal);
                var required = new HashSet<string>(StringComparer.Ordinal);
                var definitionQueue = new Queue<Element>();
                void Require(Element element)
                {
                    if (element != null && definitions.ContainsKey(element.UniqueId) && required.Add(element.UniqueId)) definitionQueue.Enqueue(element);
                }
                foreach (var uid in result.Values.SelectMany(DurableDefinitionReferences))
                    if (uid != null && definitions.TryGetValue(uid, out var definition)) Require(definition);
                foreach (var element in keep.Values)
                {
                    Require(source.GetElement(element.GetTypeId()));
                    foreach (Parameter parameter in element.Parameters)
                        if (parameter.HasValue && parameter.StorageType == StorageType.ElementId) Require(source.GetElement(parameter.AsElementId()));
                }
                while (definitionQueue.Count > 0)
                {
                    var definition = definitionQueue.Dequeue();
                    if (definition is FamilySymbol symbol) Require(symbol.Family);
                    foreach (Parameter parameter in definition.Parameters)
                        if (parameter.HasValue && parameter.StorageType == StorageType.ElementId) Require(source.GetElement(parameter.AsElementId()));
                }

                using (var atomic = new TransactionGroup(source, "BIMaestro - Préparer le sous-ensemble historique natif"))
                {
                    atomic.Start();
                    SetStage("prune_unrequested_physical_models");
                    using (var tx = new Transaction(source, "BIMaestro - Retirer les objets hors archive native"))
                    {
                        tx.Start(); var failures = new Failures();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                        SetStage("prune_unrequested_annotations");
                        var annotations = new FilteredElementCollector(source).WhereElementIsNotElementType()
                            .Where(element => !keep.ContainsKey(element.UniqueId) && (element is SpotDimension || element is IndependentTag))
                            .Select(element => element.Id).ToList();
                        // CanDeleteElement has been available since Revit 2015,
                        // before every Revit version targeted by this project.
                        var deletableAnnotations = annotations.Where(id => DocumentValidation.CanDeleteElement(source,id)).ToList();
                        if (deletableAnnotations.Count > 0)
                        {
                            var physicalBefore = new HashSet<ElementId>(new FilteredElementCollector(source).WhereElementIsNotElementType()
                                .Where(element => !element.ViewSpecific && element.Category?.CategoryType == CategoryType.Model).Select(element => element.Id));
                            var deletedAnnotations = source.Delete(deletableAnnotations);
                            if (deletedAnnotations.Any(physicalBefore.Contains))
                                throw new InvalidOperationException("La suppression des annotations affecterait un modèle physique : nettoyage annulé.");
                        }
                        SetStage("classify_deletable_physical_models");
                        var prune = new FilteredElementCollector(source).WhereElementIsNotElementType()
                            .Where(element => !keep.ContainsKey(element.UniqueId) && !element.ViewSpecific
                                && !(element is Family || element is Material || element is Level || element is MEPSystem
                                    || element is BasePoint || element is ProjectInfo || element is Sketch || element is SketchPlane)
                                && (ElementHistoryExternalModels.IsExcluded(element)
                                    || element.Category?.CategoryType == CategoryType.Model && !new HistoryOpeningClassification {
                                        SourceElementId=element.Id.GetIdValue(), SourceUniqueId=element.UniqueId,
                                        Class=element.GetType().FullName, CategoryId=element.Category.Id.GetIdLongValue(), ViewSpecific=element.ViewSpecific
                                    }.IsExcluded()))
                            // Deleting one member edits every replica of its group. Prune native roots only.
                            .Where(element => OpeningRoot(element).Id == element.Id)
                            .Select(element => element.Id).ToList();
                        var externalTypes = new FilteredElementCollector(source).WhereElementIsElementType()
                            .Where(element => ElementHistoryExternalModels.IsExcluded(element) && !externalContext.Contains(element.Id)).Select(element => element.Id);
                        var candidates = prune.Concat(externalTypes).Distinct().ToList();
                        var blocked = candidates.Where(id => !DocumentValidation.CanDeleteElement(source,id)).ToList();
                        nonDeletableCount = blocked.Count;
                        var blockedSet = new HashSet<ElementId>(blocked);
                        var remove = candidates.Where(id => !blockedSet.Contains(id)).ToList();
                        pruneCount = remove.Count;
                        if (manual)
                        {
                            File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-subset-prune-diagnostic.json"),
                                Newtonsoft.Json.JsonConvert.SerializeObject(new { AtUtc=DateTime.UtcNow, Keep=keep.Count, Candidates=candidates.Count,
                                    Prune=remove.Count, NonDeletable=blocked.Select(id => { var e=source.GetElement(id); return new {
                                        Id=id.GetIdLongValue(), Class=e?.GetType().FullName, Name=e?.Name, UniqueId=e?.UniqueId }; }).ToList(),
                                    NonDeletableAnnotations=annotations.Except(deletableAnnotations).Select(id => { var e=source.GetElement(id); return new {
                                        Id=id.GetIdLongValue(), Class=e?.GetType().FullName, Name=e?.Name }; }).ToList()
                                },Newtonsoft.Json.Formatting.Indented));
                        }
                        SetStage("delete_unrequested_physical_models");
                        if (remove.Count > 0) source.Delete(remove);
                        var lost = keepIdentities.Values.Where(value=>source.GetElement(value.UniqueId)==null).ToList();
                        if (lost.Count > 0)
                        {
                            if (manual)
                                File.WriteAllText(Path.Combine(CollaborativeModelTrackerStore.ActiveDirectory,"native-subset-protected-lost.json"),
                                    Newtonsoft.Json.JsonConvert.SerializeObject(new { AtUtc=DateTime.UtcNow, Protected=lost },Newtonsoft.Json.Formatting.Indented));
                            throw new InvalidOperationException("Le nettoyage supprimerait un objet protégé de l’archive : " + lost[0].UniqueId
                                + "; class=" + lost[0].Class + "; category=" + lost[0].Category + "; name=" + lost[0].Name + "; root=" + lost[0].RootUniqueId);
                        }
                        if (tx.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Le nettoyage natif a été refusé par Revit. " + failures.Message);
                    }
                    RecordPlacements("after-prune");
                    foreach(var pair in result)
                        if(!ElementHistoryReconstruction.VerifyHistoricalPlacement(source.GetElement(pair.Key),pair.Value,out var pruneFailure,true))
                            throw new InvalidOperationException("Le nettoyage a modifié un placement historique : "+pair.Key+" : "+pruneFailure);
                    SetStage("stamp_native_subset_identities");
                    using (var tx = new Transaction(source, "BIMaestro - Conserver les identités de l’archive native"))
                    {
                        tx.Start(); ConfigureDurableFailures(tx);
                        foreach (var uid in keep.Keys)
                        {
                            var element = source.GetElement(uid);
                            if (element == null) throw new InvalidOperationException("Objet protégé absent après nettoyage : " + uid);
                            var schema = NativeCopySchema(); var entity = new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);
                            entity.Set(schema.GetField("CopySourceUniqueId"), uid); entity.Set(schema.GetField("HistoricalUniqueId"), uid);
                            element.SetEntity(entity);
                            ElementHistoryRestoration.SetOrigins(element, (ElementHistoryRestoration.GetOrigins(element) ?? new List<string>()).Concat(new[] {uid}));
                        }
                        SetStage("version_native_subset_definitions");
                        foreach (var pair in definitions)
                        {
                            var original = pair.Value;
                            var definition = source.GetElement(pair.Key);
                            if (definition == null || ElementHistoryExternalModels.IsExcluded(definition)) continue;
                            var oldName = definition.Name;
                            if (definition is ElementType type && !type.CanBeRenamed)
                            {
                                if (required.Contains(pair.Key)) throw new InvalidOperationException("La définition native requise ne peut pas être versionnée : " + oldName);
                                continue;
                            }
                            StampDurableDefinitionPair(original, definition, version);
                            StampDurableDefinition(definition, version);
                            if (definition is Level) continue; // Keep original names and elevations.
                            if (definition is Family || definition is ElementType || definition is Material)
                            {
                                try { definition.Name = VersionedDefinitionName(oldName, version, pair.Key); }
                                catch (Exception failure) when (!(failure is Autodesk.Revit.Exceptions.RegenerationFailedException) && !required.Contains(pair.Key))
                                { Trace.WriteLine("Unused native definition retained: " + pair.Key + " " + failure.Message); }
                            }
                        }
                        CommitDurableTransaction(tx);
                    }
                    RecordPlacements("after-definitions");
                    SetStage("verify_frozen_native_subset");
                    var mapping = keep.Keys.ToDictionary(uid => uid, uid => source.GetElement(uid), StringComparer.Ordinal);
                    VerifyNativeSetConnections(source, mapping, source);
                    VerifyNativeHosts(source, mapping, source);
                    ElementHistoryRestoration.VerifyNativeSystems(source, mapping, source);
                    VerifyNativeSubsetProofs(source, proofs);
                    foreach (var item in items)
                        if (!ElementHistoryReconstruction.VerifyHistoricalPlacement(source.GetElement(item.Element.UniqueId), result[item.Element.UniqueId], out var failure, true))
                            throw new InvalidOperationException("Le sous-ensemble natif a modifié le placement historique : " + item.Element.UniqueId + " : " + failure);
                    if(SnapshotExternalModelIds(source).Any(id=>!externalContext.Contains(id)))
                        throw new InvalidOperationException("Un modèle externe sans rôle de contrainte subsiste dans l’archive native.");
                    if (atomic.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Le sous-ensemble natif n’a pas été validé.");
                }
                SetStage("save_partial_native_snapshot_as_new_file");
                Directory.CreateDirectory(Path.GetDirectoryName(outputRvt));
                using (var options = new SaveAsOptions { Compact = true, MaximumBackups = 1, OverwriteExistingFile = false })
                {
                    if (source.IsWorkshared)
                    {
                        using (var shared = new WorksharingSaveAsOptions { SaveAsCentral = true }) options.SetWorksharingOptions(shared);
                    }
                    source.SaveAs(outputRvt, options);
                }
                SetStage("partial_native_snapshot_saved");
                return result;
            }
            catch (Exception failure)
            {
                var error = new InvalidOperationException("Export natif partiel refusé [stage=" + stage + "; source=" + originalPath
                    + "; destination=" + outputRvt + "] " + failure.Message, failure);
                Trace.WriteLine(error.ToString()); throw error;
            }
        }

        private static void VerifyNativeSubsetProofs(Document document, IEnumerable<NativeSubsetProof> proofs)
        {
            foreach (var proof in proofs)
            {
                var actual = document.GetElement(proof.UniqueId);
                if (actual == null) throw new InvalidOperationException("Objet natif perdu pendant le nettoyage : " + proof.UniqueId);
                if (proof.Host != null && (!(actual is FamilyInstance family) || family.Host?.UniqueId != proof.Host))
                    throw new InvalidOperationException("Support natif modifié pendant le nettoyage : " + proof.UniqueId);
                foreach (var edge in proof.Connections)
                {
                    var a = ElementHistoryNetwork.Match(actual, edge.Port);
                    var b = ElementHistoryNetwork.Match(document.GetElement(edge.Peer), edge.PeerPort);
                    if (a == null || b == null || !a.IsConnectedTo(b)) throw new InvalidOperationException("Connexion native perdue pendant le nettoyage : " + proof.UniqueId);
                }
                foreach (var saved in proof.Systems)
                {
                    var matches = ElementHistoryNetwork.Ports(actual).Where(port => port.Domain == saved.Item2 && port.Shape == saved.Item3
                        && port.Origin.DistanceTo(saved.Item1) < 1e-5).ToList();
                    if (matches.Count != 1 || matches[0].MEPSystem?.UniqueId != saved.Item4)
                        throw new InvalidOperationException("Connecteur ou système natif modifié pendant le nettoyage : " + proof.UniqueId);
                }
            }
        }
    }
}

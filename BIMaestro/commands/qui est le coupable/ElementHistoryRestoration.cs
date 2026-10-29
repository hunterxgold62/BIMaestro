using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Analyse
{
    internal sealed class HistoryRestoreRequest
    {
        public string SourceUniqueId { get; set; }
        public string Label { get; set; }
        public HistoryRecipe Recipe { get; set; }
        public string CaptureFailure { get; set; }
        public string Category { get; set; }
        public string SuperComponentUniqueId { get; set; }
        public string FamilyTypeUniqueId { get; set; }
        public HistoryDeletionElementAudit DeletionAudit { get; set; }
        // Runtime-only: a native retry must not choose the direct recipe again.
        internal bool ForceNativeOpeningRestore { get; set; }
        internal HistoryOpeningClassification OpeningClassification;
        internal bool ClassificationOnly;
        internal bool NativeGeneration;
    }

    internal sealed class HistoryRestoreItem
    {
        public string Label { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
        public string UniqueId { get; set; }
        public bool Created { get; set; }
        public bool Existing { get; set; }
        public bool IncludedInParent { get; set; }
        public bool Repaired { get; set; }
        public string SourceUniqueId { get; set; }
        public string Category { get; set; }
        public string RecipeKind { get; set; }
        public string OpeningFallbackTrigger { get; set; }
        public HistorySystemTypeAdjustment SystemTypeAdjustment { get; set; }
        public string NativeRootSourceUniqueId { get; set; }
        public string HostSourceUniqueId { get; set; }
        public string SuperComponentSourceUniqueId { get; set; }
        public List<string> SketchCurveSourceUniqueIds { get; set; }
        public HistoryDeletionElementAudit DeletionAudit { get; set; }
        public string ParentSourceUniqueId { get; set; }
        public string ParentOutcome { get; set; }
    }

    internal sealed class HistorySystemTypeAdjustment
    {
        public bool SourceDefined { get; set; }
        public string SourceTypeUniqueId { get; set; }
        public string SourceTypeName { get; set; }
        public bool ActualDefined { get; set; }
        public string ActualTypeUniqueId { get; set; }
        public string ActualTypeName { get; set; }
        public List<string> SourceConnectedPeerTypeUniqueIds { get; set; }
        public List<string> ConnectedPeerTypeUniqueIds { get; set; }
        public bool MatchesUniqueConnectedPeerType { get; set; }
        public bool RevitAssignedPreviouslyUndefinedType { get; set; }
        public bool ClassificationPreserved { get; set; }
        public string Note { get; set; }
    }

    internal sealed class HistoryRestoreBatch
    {
        public List<HistoryRestoreItem> Items { get; } = new List<HistoryRestoreItem>();
        public int Created => Items.Count(i => i.Created);
        public int Existing => Items.Count(i => i.Existing);
        public int IncludedInParent => Items.Count(i => i.IncludedInParent);
        public int Failed => Items.Count(i => !i.Created && !i.Existing && !i.IncludedInParent);
        public int ConnectionsRestored { get; set; }
        public int ConnectionsExisting { get; set; }
        public int RelationsRestored { get; set; }
        public int RelationsExisting { get; set; }
        public List<string> RelationFailures { get; } = new List<string>();
        public List<HistoryRelationAudit> RelationAudit { get; } = new List<HistoryRelationAudit>();
        public List<string> ConnectionFailures { get; } = new List<string>();
        public List<string> CaptureWarnings { get; } = new List<string>();
        public int Repaired => Items.Count(i => i.Repaired);
        public List<string> RepairFailures { get; } = new List<string>();
    }

    internal static partial class ElementHistoryRestoration
    {
        // Insulation remains deliberately deleted, including generic families
        // named calorifuge and older histories without reconstruction recipes.
        internal static bool IsCalorifuge(string category, string label)
        {
            var name = (category ?? "").Trim();
            return name.IndexOf("calorifuge", StringComparison.OrdinalIgnoreCase) >= 0
                || (label ?? "").IndexOf("calorifuge", StringComparison.OrdinalIgnoreCase) >= 0
                || new[] { "Pipe Insulations", "Pipe Insulation", "Duct Insulations", "Duct Insulation",
                    "Isolants de canalisation", "Isolant de canalisation", "Isolation de canalisation",
                    "Isolants de gaine", "Isolant de gaine", "Isolation de gaine" }
                    .Any(s => string.Equals(name, s, StringComparison.OrdinalIgnoreCase));
        }

        // Saved on each native element. Survives project save/reopen and follows Undo.
        private static readonly Guid OriginSchemaId = new Guid("4b0d2453-93dd-4b44-9280-d2758acb4a84");
        private static Schema OriginSchema()
        {
            var schema = Schema.Lookup(OriginSchemaId);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(OriginSchemaId);
            builder.SetSchemaName("BIMaestroHistoryRestorationV1");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField("OriginalUniqueIds", typeof(string));
            return builder.Finish();
        }

        internal static List<string> GetOrigins(Element element)
        {
            if (element == null || !element.IsValidObject) return null;
            var schema = Schema.Lookup(OriginSchemaId);
            if (schema == null) return null;
            var entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<IList<string>>(schema.GetField("OriginalUniqueIds")).ToList() : null;
        }

        internal static void SetOrigins(Element element, IEnumerable<string> origins)
        {
            var schema = OriginSchema();
            var entity = new Entity(schema);
            entity.Set<IList<string>>(schema.GetField("OriginalUniqueIds"), origins.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList());
            element.SetEntity(entity);
        }

        private static Dictionary<string, string> ReadIndex(Document doc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Schema.Lookup(OriginSchemaId) == null) return result;
            foreach (var element in new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(OriginSchemaId)))
                foreach (var origin in GetOrigins(element) ?? new List<string>())
                    result[origin] = element.UniqueId;
            return result;
        }

        private static string Resolve(Document doc, Dictionary<string, string> index, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (ElementHistoryReconstruction.FindOriginal(doc, id) != null) return id;
            return index.TryGetValue(id, out var restored)
                && ElementHistoryReconstruction.FindOriginal(doc, restored) != null ? restored : null;
        }

        internal static HistoryRestoreBatch Restore(Document doc, IEnumerable<HistoryRestoreRequest> requests,
            Action refreshView = null)
        {
            if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable)
                throw new InvalidOperationException("Restoration requires an editable project outside a transaction.");
            var batch = new HistoryRestoreBatch();
            var display = new RestoreDisplay(refreshView);
            var index = ReadIndex(doc);
            using var nativeSources = new ElementHistoryNativeArchive.Sources();
            var openingFailures = new Dictionary<HistoryRestoreRequest, string>();
            var selected = (requests ?? Enumerable.Empty<HistoryRestoreRequest>()).Where(r => r != null)
                .Select(r => new HistoryRestoreRequest { SourceUniqueId = r.SourceUniqueId, Label = r.Label,
                    Recipe = r.Recipe == null ? null : JObject.FromObject(r.Recipe).ToObject<HistoryRecipe>(),
                    Category = r.Category, CaptureFailure = r.CaptureFailure,
                    SuperComponentUniqueId = r.SuperComponentUniqueId, FamilyTypeUniqueId = r.FamilyTypeUniqueId,
                    DeletionAudit = r.DeletionAudit, ForceNativeOpeningRestore = r.ForceNativeOpeningRestore }).ToList();
            foreach (var request in selected.Where(request => request.Recipe?.UsesExistingModelDefinitions == true))
                UseExistingModelDefinitionsOnly(request.Recipe);
            // Events deleted before inventory have only their numeric source ID.
            // Resolve their identities before de-duplication and dependency order.
            // Opening the checkpoint happens only for this explicit user action.
            for (int i = 0; i < selected.Count; i++)
            {
                var request = selected[i];
                if (request.Recipe?.UsesExistingModelDefinitions == true || request.Recipe?.Native?.WholeModel != true) continue;
                try
                {
                    var source = nativeSources.Open(doc, request.Recipe.Native);
                    selected[i] = ElementHistoryNativeArchive.HydrateOpeningRequest(source, request);
                }
                catch (Exception ex) { openingFailures[request] = ex.Message; }
            }
            selected = selected.GroupBy(r => !string.IsNullOrWhiteSpace(r.SourceUniqueId) ? r.SourceUniqueId
                    : r.Recipe?.Native?.WholeModel == true ? "opening:" + r.Recipe.Native.File + ":" + r.Recipe.Native.SourceElementId
                    : r.Label ?? string.Empty).Select(g => g.First())
                .Where(r => !IsCalorifuge(r.Category, r.Label))
                .OrderBy(r => r.Recipe?.Kind == "family" ? 1 : 0).ToList();
            selected = OrderByDependencies(selected);
            // A deleted device can leave its circuit alive with fewer members. Replay
            // the circuit captured on the device even when no circuit deletion exists.
            var explicitIds = new HashSet<string>(selected.Select(r => r.SourceUniqueId));
            var companionCircuits = selected.SelectMany(r => r.Recipe?.ElectricalCircuits ?? new List<HistoryCircuit>())
                .Where(c => c.Recipe != null && !explicitIds.Contains(c.SourceUniqueId))
                .GroupBy(c => c.SourceUniqueId).Select(g => g.First())
                .Select(c => new HistoryRestoreRequest { SourceUniqueId = c.SourceUniqueId, Label = "Circuit électrique", Recipe = c.Recipe }).ToList();
            selected.AddRange(companionCircuits);
            selected = OrderByDependencies(selected);
            // Keep one Undo even when an unsuccessful direct recipe needs a
            // second, native attempt after its first transaction group closes.
            using var completeGroup = selected.Any(r => r.Recipe?.UsesExistingModelDefinitions != true
                    && IsRestorationFallback(r.Recipe?.OpeningFallback))
                ? new TransactionGroup(doc, "BIMaestro - Restaurer historique avec secours d’ouverture") : null;
            completeGroup?.Start();
            var generatedSketchOrigins = new HashSet<string>(StringComparer.Ordinal);
            var networksFinalized = false;
            using (var group = new TransactionGroup(doc, "BIMaestro - Restaurer éléments supprimés"))
            {
                group.Start();
                foreach (var request in selected)
                {
                    var result = new HistoryRestoreItem
                    {
                        Label = request.Label, SourceUniqueId = request.SourceUniqueId, Category = request.Category,
                        RecipeKind = request.Recipe?.Kind,
                        NativeRootSourceUniqueId = request.Recipe?.Native?.RootSourceUniqueId,
                        HostSourceUniqueId = request.Recipe?.Host,
                        SuperComponentSourceUniqueId = request.SuperComponentUniqueId,
                        SketchCurveSourceUniqueIds = request.Recipe?.SketchCurves?.Select(c => c.SourceUniqueId)
                            .Where(uid => !string.IsNullOrWhiteSpace(uid)).ToList(),
                        DeletionAudit = request.DeletionAudit
                    };
                    batch.Items.Add(result);
                    if (openingFailures.TryGetValue(request, out var openingFailure))
                    { result.Reason = "archive"; result.Detail = openingFailure; continue; }
                    foreach (var warning in request.Recipe?.CaptureWarnings ?? new List<string>())
                        batch.CaptureWarnings.Add(request.Label + " : " + warning);
                    if (string.IsNullOrEmpty(request.SourceUniqueId)) { result.Reason = "identity"; continue; }
                    // Revit forms piping/duct systems from connected members; recreating
                    // one splits the network. Members reconnect and rejoin it instead.
                    if (request.Recipe?.Kind == "pipe_system" || request.Recipe?.Kind == "duct_system")
                    {
                        result.IncludedInParent = true;
                        result.Detail = "Système recomposé par Revit à partir des raccords rétablis.";
                        continue;
                    }
                    var existing = Resolve(doc, index, request.SourceUniqueId);
                    if (existing != null && request.Recipe?.Kind != "electrical_system")
                    {
                        result.IncludedInParent = generatedSketchOrigins.Contains(request.SourceUniqueId);
                        result.Existing = !result.IncludedInParent; result.UniqueId = existing; continue;
                    }
                    var recipe = ElementHistoryReconstruction.ReadRecipe(request.Recipe);
                    if (recipe == null) { result.Reason = "recipe"; result.Detail = request.CaptureFailure
                        ?? "Cet événement ne contient ni recette exploitable ni diagnostic de capture ; la cause d’origine n’a pas été enregistrée."; continue; }
                    // Clone before remapping dependencies. The historical payload is immutable.
                    recipe = JObject.FromObject(recipe).ToObject<HistoryRecipe>();
                    recipe.ExistingElectricalSystem = existing;
                    if (recipe.UsesExistingModelDefinitions)
                    {
                        try { ValidateExistingModelDefinitions(doc, recipe, index); }
                        catch (InvalidOperationException ex)
                        {
                            result.Reason = ex.Message.StartsWith("ExistingModelDefinitionMissing:", StringComparison.Ordinal)
                                ? "definition_missing" : "reference_missing";
                            result.Detail = ex.Message; continue;
                        }
                        // This mode keeps only the instance recipe. Even a stale
                        // fallback field cannot import or duplicate definitions.
                        recipe.Native = null; recipe.OpeningFallback = null;
                    }
                    if (recipe.SystemMembers != null)
                    {
                        if (!networksFinalized)
                        {
                            display.Flush();
                            RepairRestoredFittings(doc, selected, index, batch);
                            Reconnect(doc, selected, index, batch);
                            networksFinalized = true;
                        }
                        recipe.SystemMembers = recipe.SystemMembers.Select(id => Resolve(doc,index,id) ?? id).ToList();
                    }
                    Document nativeSource = null;
                    Element nativeParent = null;
                    if (recipe.Native != null)
                    {
                        try
                        {
                            nativeSource = nativeSources.Open(doc, recipe.Native);
                            nativeParent = ElementHistoryReconstruction.FindOriginal(doc, Resolve(doc, index, recipe.Native.RootSourceUniqueId));
                        }
                        catch (Exception ex) { result.Reason = "archive"; result.Detail = ex.Message; continue; }
                    }
                    if (recipe.OpeningFallback?.DefinitionOnly == true)
                    {
                        try { ImportDurableDefinition(doc, recipe, nativeSources, index); }
                        catch (Exception ex) { result.Reason = "definition"; result.Detail = ex.Message; continue; }
                    }
                    if (recipe.Native == null && recipe.Kind != "electrical_system" && ElementHistoryReconstruction.FindOriginal(doc, recipe.Type) == null) { result.Reason = "type"; continue; }
                    if (recipe.Native == null && recipe.SystemMembers == null && !(ElementHistoryReconstruction.FindOriginal(doc, recipe.Level) is Level)) { result.Reason = "level"; continue; }
                    if (!string.IsNullOrEmpty(recipe.Host))
                    {
                        recipe.Host = Resolve(doc, index, recipe.Host);
                        if (recipe.Host == null) { result.Reason = "host"; continue; }
                    }
                    foreach (var parameter in recipe.Parameters ?? new List<HistoryParameter>())
                        if (!string.IsNullOrEmpty(parameter.Reference))
                            parameter.Reference = Resolve(doc, index, parameter.Reference) ?? parameter.Reference;

                    using (var creationGroup = new TransactionGroup(doc, "BIMaestro - Vérifier création historique"))
                    using (var transaction = new Transaction(doc, "BIMaestro - Restaurer élément supprimé"))
                    {
                        creationGroup.Start();
                        transaction.Start();
                        var failures = new RestoreFailures();
                        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                            .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                        try
                        {
                            Dictionary<string, string> sketchOrigins;
                            Element element;
                            if (nativeSource != null)
                            {
                                element = ElementHistoryNativeArchive.Restore(doc, recipe, nativeSource, nativeParent, out sketchOrigins);
                                ElementHistoryReconstruction.RestoreNativeMetadata(doc, element, recipe);
                            }
                            else
                            {
                                element = recipe.UsesExistingModelDefinitions
                                    ? RestoreWithExistingModelDefinitions(doc, recipe, index)
                                    : ElementHistoryReconstruction.RestoreNative(doc, recipe);
                                sketchOrigins = MarkSketchOrigins(doc, element, recipe);
                            }
                            var origins = (recipe.RestorationOrigins ?? new List<string>())
                                .Concat(GetOrigins(element) ?? new List<string>())
                                .Concat(new[] { request.SourceUniqueId }).Distinct().ToList();
                            var schema = OriginSchema();
                            var entity = new Entity(schema);
                            entity.Set<IList<string>>(schema.GetField("OriginalUniqueIds"), origins);
                            element.SetEntity(entity);
                            string uid = element.UniqueId;
                            if (transaction.Commit() == TransactionStatus.Committed)
                            {
                                // Revit can auto-correct a commit by removing the created
                                // object. Never count a transient object as restored.
                                if (ElementHistoryReconstruction.FindOriginal(doc, uid) == null)
                                {
                                    result.Reason = "creation";
                                    result.Detail = "Revit a supprimé l’élément lors de la validation. " + failures.Message;
                                }
                                else
                                {
                                    if (!ElementHistoryReconstruction.VerifyHistoricalPlacement(doc.GetElement(uid), recipe,
                                        out var placementFailure))
                                        throw new InvalidOperationException("Placement après validation : " + placementFailure);
                                    if (creationGroup.Assimilate() != TransactionStatus.Committed)
                                        throw new InvalidOperationException("La création historique n’a pas pu être validée.");
                                    result.Created = existing == null; result.Existing = existing != null;
                                    result.Repaired = existing != null && recipe.ElectricalChanged;
                                    result.UniqueId = uid; result.Detail = failures.Message;
                                    foreach (var origin in origins) index[origin] = uid;
                                    foreach (var child in sketchOrigins)
                                        if (doc.GetElement(child.Value) != null)
                                        { index[child.Key] = child.Value; generatedSketchOrigins.Add(child.Key); }
                                }
                            }
                            else
                            {
                                result.Reason = "creation"; result.Detail = failures.Message;
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (Exception ex)
                        {
                            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                            if (creationGroup.GetStatus() == TransactionStatus.Started) creationGroup.RollBack();
                            result.Reason = "creation"; result.Detail = ex.Message;
                        }
                    }
                    // Only repaint after the element transaction has finished. Keep
                    // the group open so the entire restoration remains one Undo.
                    if (result.Created) display.ElementCreated();
                }
                display.Flush();
                // Nested instances are recreated by Revit with their parent. Verify that
                // the parent and a child of the recorded type actually exist before
                // removing their historical entries from the failure count.
                var matchedNestedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var request in selected.Where(r => r.Recipe == null
                    && !string.IsNullOrEmpty(r.SuperComponentUniqueId)
                    && !string.IsNullOrEmpty(r.FamilyTypeUniqueId)))
                {
                    var result = batch.Items.FirstOrDefault(i => i.SourceUniqueId == request.SourceUniqueId);
                    if (result == null || result.Created || result.Existing) continue;
                    var parentUid = Resolve(doc, index, request.SuperComponentUniqueId);
                    var parent = ElementHistoryReconstruction.FindOriginal(doc, parentUid) as FamilyInstance;
                    if (parent == null) continue;
                    var child = parent.GetSubComponentIds()
                        .Select(doc.GetElement).OfType<FamilyInstance>()
                        .FirstOrDefault(i => i.Symbol?.UniqueId == request.FamilyTypeUniqueId
                            && !matchedNestedIds.Contains(i.UniqueId));
                    if (child == null) continue;
                    matchedNestedIds.Add(child.UniqueId);
                    result.IncludedInParent = true;
                    result.UniqueId = child.UniqueId;
                    result.Reason = null;
                    result.Detail = "Sous-composant recréé avec sa famille parente.";
                }
                if (!networksFinalized)
                {
                    RepairRestoredFittings(doc, selected, index, batch);
                    Reconnect(doc, selected, index, batch);
                }
                RestoreUndefinedSystems(doc, selected, batch);
                ElementHistoryRelations.Restore(doc, selected, index, batch);
                VerifyFinalPlacements(doc, selected, batch);
                if (batch.Created > 0 || batch.ConnectionsRestored > 0 || batch.Repaired > 0 || batch.RelationsRestored > 0)
                {
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Restoration could not be committed.");
                }
                else group.RollBack();
            }
            ElementHistoryRelations.Invalidate(doc);
            ObserveSystemTypeAdjustments(doc, selected, batch);
            // Dispose is idempotent. A primary mixed batch may already have opened
            // this checkpoint; close it before the fallback batch opens it once.
            nativeSources.Dispose();
            RetryOpeningFailures(doc, selected, batch, refreshView);
            var bySource = batch.Items.Where(i => !string.IsNullOrWhiteSpace(i.SourceUniqueId))
                .GroupBy(i => i.SourceUniqueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var item in batch.Items)
            {
                var parentId = item.DeletionAudit?.SketchOwnerSourceUniqueId;
                if (string.IsNullOrWhiteSpace(parentId)) parentId = item.NativeRootSourceUniqueId;
                if (string.IsNullOrWhiteSpace(parentId) || parentId == item.SourceUniqueId)
                    parentId = item.SuperComponentSourceUniqueId ?? item.HostSourceUniqueId;
                if (string.IsNullOrWhiteSpace(parentId) || parentId == item.SourceUniqueId) continue;
                item.ParentSourceUniqueId = parentId;
                if (bySource.TryGetValue(parentId, out var parent))
                    item.ParentOutcome = parent.Created ? "created" : parent.Existing ? "already_present"
                        : parent.IncludedInParent ? "included_in_parent" : "failed";
                else item.ParentOutcome = "not_in_restore_request";
            }
            display.Flush(force: batch.ConnectionsRestored > 0 || batch.Repaired > 0 || batch.RelationsRestored > 0);
            if (completeGroup != null)
            {
                if (batch.Created > 0 || batch.ConnectionsRestored > 0 || batch.Repaired > 0 || batch.RelationsRestored > 0)
                {
                    if (completeGroup.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Restoration fallback could not be committed.");
                }
                else completeGroup.RollBack();
            }
            return batch;
        }

        private static void ObserveSystemTypeAdjustments(Document doc, List<HistoryRestoreRequest> selected,
            HistoryRestoreBatch batch)
        {
            var bySource = batch.Items.Where(item => !string.IsNullOrWhiteSpace(item.SourceUniqueId))
                .GroupBy(item => item.SourceUniqueId).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var request in selected.Where(request => SystemSource(request.Recipe)?.SourceSystemTypeDefined != null))
            {
                if (!bySource.TryGetValue(request.SourceUniqueId ?? string.Empty, out var result)
                    || !result.Created || string.IsNullOrWhiteSpace(result.UniqueId)) continue;
                var actual = ElementHistoryReconstruction.FindOriginal(doc, result.UniqueId);
                if (!ElementHistoryNativeArchive.HasMepSystemType(actual)) continue;
                var source = SystemSource(request.Recipe);
                var type = ElementHistoryNativeArchive.ReadMepSystemType(actual);
                var peerTypes = ElementHistoryNativeArchive.ReadConnectedPeerSystemTypes(actual);
                var sourcePeers = source.SourceConnectedPeerTypeUniqueIds;
                bool sourceDefined = source.SourceSystemTypeDefined.Value;
                bool actualDefined = type != null;
                bool peersMatch = actualDefined && peerTypes.Count == 1 && peerTypes[0] == type.UniqueId
                    && (sourcePeers == null || sourcePeers.Count == 0
                        || (sourcePeers.Count == 1 && sourcePeers[0] == type.UniqueId));
                var adjustment = new HistorySystemTypeAdjustment
                {
                    SourceDefined = sourceDefined, SourceTypeUniqueId = source.SourceSystemTypeUniqueId,
                    SourceTypeName = source.SourceSystemTypeName, ActualDefined = actualDefined,
                    ActualTypeUniqueId = type?.UniqueId, ActualTypeName = type?.Name,
                    SourceConnectedPeerTypeUniqueIds = sourcePeers, ConnectedPeerTypeUniqueIds = peerTypes,
                    MatchesUniqueConnectedPeerType = peersMatch,
                    RevitAssignedPreviouslyUndefinedType = !sourceDefined && actualDefined,
                    ClassificationPreserved = sourceDefined == actualDefined
                        && (!sourceDefined || ElementHistoryNetwork.HistoricalSystemTypeMatches(type, source.SourceSystemTypeUniqueId, source.TypeVersion))
                };
                result.SystemTypeAdjustment = adjustment;
                if (adjustment.ClassificationPreserved) continue;
                if (!sourceDefined && actualDefined && peersMatch)
                {
                    var name = actual is Autodesk.Revit.DB.Plumbing.Pipe || actual is Autodesk.Revit.DB.Plumbing.FlexPipe
                        ? "cette canalisation" : "cette gaine";
                    adjustment.Note = "Revit a rattaché " + name
                        + " au système des voisins reconnectés ; aucun système n’était défini dans l’historique.";
                }
                else if (!sourceDefined && actualDefined)
                    adjustment.Note = "Revit a attribué un type de système pendant la restauration ; l’état initial sans système n’a pas été conservé. Le système des voisins ne permet pas de confirmer cette attribution.";
                else adjustment.Note = "Le type de système restauré diffère du type historique ; sa classification n’a pas été conservée.";
                result.Detail = string.IsNullOrWhiteSpace(result.Detail) ? adjustment.Note : result.Detail + " " + adjustment.Note;
            }
        }

        private static HistoryNativeArtifact SystemSource(HistoryRecipe recipe) => recipe?.Native ?? recipe?.OpeningFallback;

        private static void RestoreUndefinedSystems(Document doc, List<HistoryRestoreRequest> selected,
            HistoryRestoreBatch batch)
        {
            var restored = batch.Items.Where(item => item.Created && item.UniqueId != null)
                .GroupBy(item => item.SourceUniqueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var request in selected.Where(r => SystemSource(r.Recipe)?.SourceSystemTypeDefined == false))
            {
                if (request.SourceUniqueId == null || !restored.TryGetValue(request.SourceUniqueId, out var item)) continue;
                var element = doc.GetElement(item.UniqueId);
                if (ElementHistoryNativeArchive.ReadMepSystemType(element) == null) continue;
                using (var attempt = new TransactionGroup(doc, "BIMaestro - Vérifier système historique indéfini"))
                {
                    attempt.Start();
                    try
                    {
                        var guard = ElementHistoryNetwork.CaptureConnectionGuard(doc, element, null, request.Recipe);
                        using (var tx = new Transaction(doc, "BIMaestro - Rétablir système historique indéfini"))
                        {
                            tx.Start();
                            var failures = new RestoreFailures();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                            if (!ElementHistoryNetwork.RestoreUndefinedSystemType(doc, element, request.Recipe, out var failure))
                                throw new InvalidOperationException(failure);
                            if (tx.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException(failures.Message ?? "Système historique refusé par Revit.");
                        }
                        element = doc.GetElement(item.UniqueId);
                        if (ElementHistoryNativeArchive.ReadMepSystemType(element) != null)
                            throw new InvalidOperationException("Revit a réattribué un système lors de la validation.");
                        ElementHistoryNetwork.VerifyConnectionGuard(doc, guard);
                        if (attempt.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Le système historique indéfini n’a pas pu être validé.");
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Exception ex)
                    {
                        if (attempt.GetStatus() == TransactionStatus.Started) attempt.RollBack();
                        batch.RepairFailures.Add(request.Label + " : " + ex.Message);
                    }
                }
            }
        }

        private static void VerifyFinalPlacements(Document doc, List<HistoryRestoreRequest> selected, HistoryRestoreBatch batch)
        {
            var recipes = selected.Where(r => r.SourceUniqueId != null).GroupBy(r => r.SourceUniqueId)
                .ToDictionary(g => g.Key, g => g.First().Recipe, StringComparer.Ordinal);
            foreach (var item in batch.Items.Where(i => i.Created || i.IncludedInParent))
                if (item.UniqueId != null && recipes.TryGetValue(item.SourceUniqueId ?? "", out var recipe)
                    && !(doc.GetElement(item.UniqueId) is Group)
                    && !ElementHistoryReconstruction.VerifyHistoricalPlacement(doc.GetElement(item.UniqueId), recipe,
                        out var failure, includeBoundingBox: true))
                    batch.RepairFailures.Add(item.Label + " : placement après rétablissement des relations : " + failure);
        }

        private static void RetryOpeningFailures(Document doc, List<HistoryRestoreRequest> selected,
            HistoryRestoreBatch batch, Action refreshView)
        {
            var failed = batch.Items.Where(item => !item.Created && !item.Existing && !item.IncludedInParent
                    && !string.IsNullOrWhiteSpace(item.SourceUniqueId))
                .GroupBy(item => item.SourceUniqueId).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            if (failed.Count == 0) return;
            var backupRequests = selected.Where(request => request.Recipe?.UsesExistingModelDefinitions != true
                    && IsRestorationFallback(request.Recipe?.OpeningFallback)
                    && !string.IsNullOrWhiteSpace(request.SourceUniqueId) && failed.ContainsKey(request.SourceUniqueId))
                .Select(request => new HistoryRestoreRequest
                {
                    SourceUniqueId = request.SourceUniqueId, Label = request.Label, Category = request.Category,
                    ForceNativeOpeningRestore = true,
                    SuperComponentUniqueId = request.SuperComponentUniqueId, FamilyTypeUniqueId = request.FamilyTypeUniqueId,
                    DeletionAudit = request.DeletionAudit,
                    Recipe = new HistoryRecipe
                    {
                        Kind = "native_archive", Native = request.Recipe.OpeningFallback,
                        HistoricalPlacement = request.Recipe.HistoricalPlacement,
                        Connections = request.Recipe.Connections,
                        GeometryRelations = request.Recipe.GeometryRelations,
                        Parameters = request.Recipe.Parameters,
                        NativeFallbackReason = "La recette directe a échoué : "
                            + (failed[request.SourceUniqueId].Detail ?? failed[request.SourceUniqueId].Reason)
                    }
                }).ToList();
            if (backupRequests.Count == 0) return;
            // These fresh native recipes have no OpeningFallback, so recursion is
            // bounded to one retry. Successful direct recipes never open an RVT.
            var backup = Restore(doc, backupRequests, refreshView);
            var replacements = backup.Items.Where(item => !string.IsNullOrWhiteSpace(item.SourceUniqueId))
                .GroupBy(item => item.SourceUniqueId).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            for (int i = 0; i < batch.Items.Count; i++)
            {
                var original = batch.Items[i];
                if (string.IsNullOrWhiteSpace(original.SourceUniqueId) || !failed.ContainsKey(original.SourceUniqueId)
                    || !replacements.TryGetValue(original.SourceUniqueId, out var replacement)) continue;
                replacement.OpeningFallbackTrigger = original.Detail ?? original.Reason;
                if (!replacement.Created && !replacement.Existing && !replacement.IncludedInParent)
                    replacement.Detail = "Recette directe : " + (original.Detail ?? original.Reason)
                    + " ; copie d’ouverture : " + (replacement.Detail ?? replacement.Reason);
                batch.Items[i] = replacement;
            }
            batch.ConnectionsRestored += backup.ConnectionsRestored;
            batch.ConnectionsExisting += backup.ConnectionsExisting;
            batch.RelationsRestored += backup.RelationsRestored;
            batch.RelationsExisting += backup.RelationsExisting;
            batch.RelationFailures.AddRange(backup.RelationFailures);
            batch.RelationAudit.AddRange(backup.RelationAudit);
            batch.ConnectionFailures.AddRange(backup.ConnectionFailures);
            batch.CaptureWarnings.AddRange(backup.CaptureWarnings);
            batch.RepairFailures.AddRange(backup.RepairFailures);
        }

        private static bool IsRestorationFallback(HistoryNativeArtifact artifact) => artifact != null && !artifact.DefinitionOnly
            && (artifact.WholeModel || !string.IsNullOrWhiteSpace(artifact.PayloadId));

        private static void UseExistingModelDefinitionsOnly(HistoryRecipe recipe)
        {
            recipe.UsesExistingModelDefinitions = true;
            recipe.Native = null; recipe.OpeningFallback = null;
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>())
                if (circuit?.Recipe != null) UseExistingModelDefinitionsOnly(circuit.Recipe);
        }

        private static void ValidateExistingModelDefinitions(Document doc, HistoryRecipe recipe,
            Dictionary<string, string> index)
        {
            if (recipe?.UsesExistingModelDefinitions != true || recipe.Kind == "native_archive")
                throw new InvalidOperationException("ExistingModelReferenceMissing: recette directe incompatible.");
            Element Exact(string uid) => ElementHistoryReconstruction.FindOriginal(doc, uid);
            Element RequireReference(string uid, string role, Func<Element, bool> accepts, bool allowRestored = false)
            {
                var resolved = allowRestored ? Resolve(doc, index, uid) : uid;
                var element = Exact(resolved);
                if (string.IsNullOrWhiteSpace(uid) || element == null || !element.IsValidObject
                    || element.UniqueId != resolved || ElementHistoryExternalModels.IsExcluded(element) || !accepts(element))
                    throw new InvalidOperationException("ExistingModelReferenceMissing: role=" + role + "; UID=" + uid
                        + "; reference requise absente du modele courant.");
                return element;
            }
            if (recipe.Kind != "electrical_system")
            {
                var type = Exact(recipe.Type);
                if (type == null || !type.IsValidObject || type.UniqueId != recipe.Type
                    || ElementHistoryExternalModels.IsExcluded(type)
                    || !(type is ElementType || (recipe.Kind == "model_curve" && type is GraphicsStyle))
                    || (recipe.Kind == "family" && (!(type is FamilySymbol symbol)
                        || symbol.Family == null || !symbol.Family.IsValidObject)))
                    throw new InvalidOperationException("ExistingModelDefinitionMissing: UID=" + recipe.Type
                        + "; famille/type supprimé ou purgé du modele courant.");
            }
            if (!string.IsNullOrWhiteSpace(recipe.Level) || recipe.SystemMembers == null)
                RequireReference(recipe.Level, "niveau", element => element is Level);
            if (!string.IsNullOrWhiteSpace(recipe.HistoricalPlacement?.LevelSourceUniqueId))
                RequireReference(recipe.HistoricalPlacement.LevelSourceUniqueId, "niveau du placement", element => element is Level);
            if (!string.IsNullOrWhiteSpace(recipe.Host))
                RequireReference(recipe.Host, "hote", element => !(element is ElementType), allowRestored: true);
            if (!string.IsNullOrWhiteSpace(recipe.HistoricalPlacement?.HostSourceUniqueId))
                RequireReference(recipe.HistoricalPlacement.HostSourceUniqueId, "hote du placement",
                    element => !(element is ElementType), allowRestored: true);
            if (!string.IsNullOrWhiteSpace(recipe.Network?.SystemType))
                RequireReference(recipe.Network.SystemType, "type de systeme", element => element is MEPSystemType);
            foreach (var port in (recipe.Ports ?? new List<HistoryPort>()).Concat(
                (recipe.Connections ?? new List<HistoryConnection>()).SelectMany(link => new[] { link.Port, link.PeerPort })))
                if (!string.IsNullOrWhiteSpace(port?.SystemType))
                    RequireReference(port.SystemType, "type de systeme du connecteur", element => element is MEPSystemType);
            foreach (var parameter in (recipe.Parameters ?? new List<HistoryParameter>())
                .Concat(recipe.HistoricalPlacement?.ElevationParameters ?? new List<HistoryParameter>()))
            {
                if (!string.IsNullOrWhiteSpace(parameter.Reference))
                    RequireReference(parameter.Reference, "parametre " + parameter.Name, element => true, allowRestored: true);
                if (!string.IsNullOrWhiteSpace(parameter.Definition))
                    RequireReference(parameter.Definition, "definition du parametre " + parameter.Name, element => element is ParameterElement);
            }
        }

        private static Element RestoreWithExistingModelDefinitions(Document doc, HistoryRecipe recipe,
            Dictionary<string, string> index)
        {
            ValidateExistingModelDefinitions(doc, recipe, index);
            return ElementHistoryReconstruction.RestoreNative(doc, recipe);
        }

        private static void ImportDurableDefinition(Document doc, HistoryRecipe recipe,
            ElementHistoryNativeArchive.Sources sources, Dictionary<string, string> index)
        {
            var totalTimer = Stopwatch.StartNew();
            var phaseTimer = Stopwatch.StartNew();
            var phase = "definition_import_context";
            var definitionCount = 0; var copyCount = 0; var mappedCount = 0;
            var importSucceeded = false;
            string activeDefinition = null;
            var originalType = recipe.Type;
            void LogPhase(string name, double elapsed, bool success, object details)
            {
                try { HistoryPerformanceLog.RecordDetails(doc, name, elapsed, mappedCount,
                    Math.Max(0, definitionCount - mappedCount), success, details); }
                catch (Exception loggingFailure) { Trace.WriteLine("Definition import logging: " + loggingFailure.Message); }
            }
            try
            {
            var artifact = recipe.OpeningFallback;
            ElementHistoryNativeArchive.ValidateHistoricalContext(doc, artifact.ContextDefinitions);
            LogPhase("definition_import_context", phaseTimer.Elapsed.TotalMilliseconds, true, new { ContextCount = artifact.ContextDefinitions?.Count ?? 0 });
            phase = "definition_import_source"; phaseTimer.Restart();
            var source = sources.Open(doc, artifact);
            LogPhase(phase, phaseTimer.Elapsed.TotalMilliseconds, true, new { PayloadId = artifact.PayloadId, artifact.RootUniqueId });
            phase = "definition_import_source_map"; phaseTimer.Restart();
            var principal = source.GetElement(artifact.RootUniqueId) as ElementType;
            if (ElementHistoryExternalModels.IsExcluded(principal))
                throw new InvalidOperationException("La definition historique appartient a une maquette liee ou importee exclue de la restauration.");
            if (principal == null || ElementHistoryNativeArchive.IsPresentationDefinition(principal) || ElementHistoryNativeArchive.ReadHistoricalTypeVersion(principal) != artifact.TypeVersion
                || ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(principal) != recipe.Type)
                throw new InvalidOperationException("La definition principale n’est pas prouvee par sa capsule.");
            var definitions = new FilteredElementCollector(source).WhereElementIsElementType().ToElements()
                .Concat(new FilteredElementCollector(source).OfClass(typeof(Family)).ToElements())
                .Concat(new FilteredElementCollector(source).OfClass(typeof(Level)).ToElements())
                .Concat(new FilteredElementCollector(source).OfClass(typeof(Material)).ToElements())
                .Where(element => !ElementHistoryNativeArchive.IsPresentationDefinition(element) && ElementHistoryNativeArchive.ReadHistoricalTypeVersion(element) == artifact.TypeVersion
                    && ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(element) != null).ToList();
            if (definitions.Any(ElementHistoryExternalModels.IsExcluded))
                throw new InvalidOperationException("La fermeture historique contient une definition de maquette liee ou importee exclue de la restauration.");
            // Stock system-family containers can be present and stamped in the
            // capsule without a copyable symbol. They are not imported dependencies.
            // Every physical symbol and its loadable family still require proof.
            var symbolFamilies = new HashSet<ElementId>(definitions.OfType<FamilySymbol>()
                .Select(symbol => symbol.Family.Id));
            int ignoredFamilies = definitions.RemoveAll(definition => definition is Family
                && !symbolFamilies.Contains(definition.Id));
            var sourceIds = definitions.ToDictionary(element => element.UniqueId,
                ElementHistoryNativeArchive.ReadHistoricalDefinitionSource, StringComparer.Ordinal);
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            List<Element> ReadDestinationDefinitions() => new FilteredElementCollector(doc).WhereElementIsElementType().ToElements()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Family)).ToElements())
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Level)).ToElements())
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Material)).ToElements()).ToList();
            var destinationDefinitions = ReadDestinationDefinitions();
            var definitionUidsBeforeImport = new HashSet<string>(destinationDefinitions.Select(definition => definition.UniqueId), StringComparer.Ordinal);
            var familyUidsBeforeImport = new HashSet<string>(destinationDefinitions.OfType<Family>()
                .Select(family => family.UniqueId), StringComparer.Ordinal);
            List<Element> Matches(Element definition)
            {
                // Original project UID and historical elevation prove Level context.
                if (definition is Level expectedLevel && doc.GetElement(sourceIds[definition.UniqueId]) is Level originalLevel
                    && originalLevel.GetType() == definition.GetType()
                    && Math.Abs(originalLevel.ProjectElevation - expectedLevel.ProjectElevation) <= 1e-8)
                    return new List<Element> { originalLevel };
                return destinationDefinitions.Where(element => HasHistoricalDefinitionProof(definition, element,
                    sourceIds[definition.UniqueId], artifact.TypeVersion)).ToList();
            }
            string MappingFailure(Element definition, int count)
            {
                var original = sourceIds[definition.UniqueId];
                var candidates = destinationDefinitions.Where(element => element.GetType() == definition.GetType()
                    && (element.UniqueId == original || element.Name == definition.Name || (GetOrigins(element) ?? new List<string>()).Contains(original)))
                    .Take(12).Select(element => element.UniqueId + "/" + element.Name
                        + "/version=" + ElementHistoryNativeArchive.ReadHistoricalTypeVersion(element)
                        + "/origins=" + string.Join(",", GetOrigins(element) ?? new List<string>())
                        + (element is Level level ? "/elevation=" + level.ProjectElevation.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : ""));
                return "FrozenDefinitionMapping: matches=" + count + "; class=" + definition.GetType().FullName
                    + "; name=" + definition.Name + "; sourceUID=" + original + "; capsuleUID=" + definition.UniqueId
                    + "; expectedVersion=" + artifact.TypeVersion + "; candidates=[" + string.Join("; ", candidates) + "]";
            }
            definitionCount = definitions.Count;
            LogPhase(phase, phaseTimer.Elapsed.TotalMilliseconds, true, new { Definitions = definitionCount, DestinationDefinitions = destinationDefinitions.Count, UnreferencedFamiliesIgnored = ignoredFamilies });
            phase = "definition_import_preflight"; phaseTimer.Restart();
            ElementHistoryNativeArchive.ValidateDurableTypeCompatibility(source, doc, artifact.TypeVersion);
            using (var atomic = new TransactionGroup(doc, "BIMaestro - Importer fermeture historique"))
            {
                atomic.Start();
                phase = "definition_import_families"; phaseTimer.Restart();
                foreach (var historicalFamily in definitions.OfType<Family>())
                {
                    activeDefinition = sourceIds[historicalFamily.UniqueId];
                    var existing = Matches(historicalFamily);
                    if (existing.Count > 1) throw new InvalidOperationException(MappingFailure(historicalFamily, existing.Count));
                    if (existing.Count == 0)
                    {
                        var familyTimer = Stopwatch.StartNew();
                        ImportNewHistoricalFamily(source, doc, historicalFamily,
                            definitions.OfType<FamilySymbol>().Where(symbol => symbol.Family.Id == historicalFamily.Id).ToList(),
                            artifact.TypeVersion, familyUidsBeforeImport, definitionUidsBeforeImport);
                        destinationDefinitions = ReadDestinationDefinitions();
                        LogPhase("definition_import_family_load", familyTimer.Elapsed.TotalMilliseconds, true,
                            new { SourceUid = activeDefinition, FamilyName = historicalFamily.Name, Version = artifact.TypeVersion });
                    }
                }
                phase = "definition_import_preflight"; phaseTimer.Restart();
                var missing = new List<ElementId>();
                foreach (var definition in definitions)
                {
                    activeDefinition = sourceIds[definition.UniqueId];
                    var existing = Matches(definition);
                    if (existing.Count > 1) throw new InvalidOperationException(MappingFailure(definition, existing.Count));
                    if (existing.Count == 0 && !(definition is Family)) missing.Add(definition.Id);
                }
                copyCount = missing.Count;
                LogPhase(phase, phaseTimer.Elapsed.TotalMilliseconds, true, new { Definitions = definitionCount, Missing = copyCount });
                phase = "definition_import_copy"; phaseTimer.Restart();
                if (missing.Count > 0)
                    using (var transaction = new Transaction(doc, "BIMaestro - Importer definitions historiques"))
                    using (var options = new CopyPasteOptions())
                    {
                        transaction.Start(); options.SetDuplicateTypeNamesHandler(new ReuseProvenDefinitionCollision());
                        var apiTimer = Stopwatch.StartNew();
                        var externalBefore = ElementHistoryNativeArchive.SnapshotExternalModelIds(doc);
                        var importedIds = ElementTransformUtils.CopyElements(source, missing, doc, Transform.Identity, options);
                        ElementHistoryNativeArchive.ValidateNoExternalModelsAdded(doc, externalBefore);
                        LogPhase("definition_import_copy_api", apiTimer.Elapsed.TotalMilliseconds, true, new { Requested = missing.Count, Returned = importedIds.Count });
                        apiTimer.Restart();
                        if (transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Import de fermeture historique refuse par Revit.");
                        LogPhase("definition_import_copy_commit", apiTimer.Elapsed.TotalMilliseconds, true, new { Requested = missing.Count });
                    }
                LogPhase(phase, phaseTimer.Elapsed.TotalMilliseconds, true, new { Requested = copyCount });
                phase = "definition_import_type_identity"; phaseTimer.Restart();
                destinationDefinitions = ReadDestinationDefinitions();
                using (var identity = new Transaction(doc, "BIMaestro - Conserver identite des nouveaux types historiques"))
                {
                    identity.Start();
                    foreach (var historical in definitions.OfType<ElementType>())
                    {
                        var actual = Matches(historical);
                        if (actual.Count != 1) throw new InvalidOperationException(MappingFailure(historical, actual.Count));
                        if (!definitionUidsBeforeImport.Contains(actual[0].UniqueId))
                            ElementHistoryNativeArchive.RestoreFrozenTypeIdentity(historical, actual[0]);
                    }
                    if (identity.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("L’identite des nouveaux types historiques n’a pas ete validee.");
                }
                phase = "definition_import_verify"; phaseTimer.Restart();
                destinationDefinitions = ReadDestinationDefinitions();
                foreach (var actualSymbol in destinationDefinitions.OfType<FamilySymbol>()
                    .Where(symbol => !definitionUidsBeforeImport.Contains(symbol.UniqueId)
                        && ElementHistoryNativeArchive.ReadHistoricalTypeVersion(symbol) == artifact.TypeVersion))
                {
                    var sourceUid = ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(actualSymbol);
                    var historical = definitions.OfType<FamilySymbol>().SingleOrDefault(symbol => sourceIds[symbol.UniqueId] == sourceUid);
                    if (historical != null && !HasHistoricalDefinitionProof(historical, actualSymbol, sourceUid, artifact.TypeVersion))
                        throw new InvalidOperationException("FrozenFamilyLoad: une copie de symbole a reutilise une famille actuelle non prouvee.");
                }
                foreach (var historicalFamily in definitions.OfType<Family>())
                {
                    var links = definitions.OfType<FamilySymbol>()
                        .Where(symbol => symbol.Family.Id == historicalFamily.Id)
                        .Select(symbol => new {
                            SourceSymbolUid = sourceIds[symbol.UniqueId], CapsuleSymbolUid = symbol.UniqueId,
                            SymbolName = symbol.Name,
                            Targets = Matches(symbol).OfType<FamilySymbol>().Select(actual => new {
                                SymbolUid = actual.UniqueId, SymbolName = actual.Name,
                                SymbolVersion = ElementHistoryNativeArchive.ReadHistoricalTypeVersion(actual),
                                ParentUid = actual.Family.UniqueId, ParentName = actual.Family.Name,
                                WasPresentBeforeImport = familyUidsBeforeImport.Contains(actual.Family.UniqueId),
                                ParentVersion = ElementHistoryNativeArchive.ReadHistoricalTypeVersion(actual.Family),
                                ParentOrigins = (GetOrigins(actual.Family) ?? new List<string>()).ToArray(),
                                CategoryId = actual.Family.FamilyCategory?.Id.GetIdLongValue(),
                                Placement = actual.Family.FamilyPlacementType.ToString(),
                                Shared = actual.Family.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger(), actual.Family.IsInPlace
                            }).ToArray()
                        }).ToArray();
                    LogPhase("definition_import_family_parents", 0, true, new {
                        SourceFamilyUid = sourceIds[historicalFamily.UniqueId],
                        CapsuleFamilyUid = historicalFamily.UniqueId, FamilyName = historicalFamily.Name,
                        ExpectedVersion = artifact.TypeVersion,
                        SourceCategoryId = historicalFamily.FamilyCategory?.Id.GetIdLongValue(),
                        SourcePlacement = historicalFamily.FamilyPlacementType.ToString(),
                        SourceShared = historicalFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger(), SourceInPlace = historicalFamily.IsInPlace,
                        Symbols = links
                    });
                }
                foreach (var definition in definitions)
                {
                    activeDefinition = sourceIds[definition.UniqueId];
                    var existing = Matches(definition);
                    if (existing.Count != 1) throw new InvalidOperationException(MappingFailure(definition, existing.Count));
                    if (definition is Level expected && (!(existing[0] is Level actual)
                        || Math.Abs(expected.ProjectElevation - actual.ProjectElevation) > 1e-8))
                        throw new InvalidOperationException("Le niveau historique importe differe de la capsule.");
                    if (definition is ElementType && !definitionUidsBeforeImport.Contains(existing[0].UniqueId))
                        ElementHistoryNativeArchive.ValidateFrozenTypeIdentity(definition, existing[0]);
                    remap.Add(sourceIds[definition.UniqueId], existing[0].UniqueId);
                    mappedCount = remap.Count;
                    LogPhase("definition_import_mapping", 0, true, new { SourceUid = activeDefinition,
                        CapsuleUid = definition.UniqueId, Class = definition.GetType().FullName, Name = definition.Name,
                        TargetUid = existing[0].UniqueId, Version = ElementHistoryNativeArchive.ReadHistoricalTypeVersion(existing[0]),
                        OriginalProjectContext = existing[0].UniqueId == activeDefinition });
                }
                if (!remap.ContainsKey(recipe.Type)) throw new InvalidOperationException("Le type principal historique est absent.");
                if (atomic.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Import historique non valide.");
            }
            LogPhase(phase, phaseTimer.Elapsed.TotalMilliseconds, true, new { Mapped = mappedCount });
            phase = "definition_import_remap"; phaseTimer.Restart();
            foreach (var pair in remap) index[pair.Key] = pair.Value;
            RemapDurableDefinitionReferences(recipe, remap);
            LogPhase(phase, phaseTimer.Elapsed.TotalMilliseconds, true, new { Mapped = mappedCount });
            importSucceeded = true;
            }
            catch (Exception failure)
            {
                LogPhase(phase + "_failed", phaseTimer.Elapsed.TotalMilliseconds, false, new {
                    SourceUid = activeDefinition, RecipeType = originalType,
                    PayloadId = recipe.OpeningFallback?.PayloadId, Version = recipe.OpeningFallback?.TypeVersion,
                    Definitions = definitionCount, RequestedCopies = copyCount, Mapped = mappedCount, Error = failure.ToString() });
                throw new InvalidOperationException("DefinitionImport [phase=" + phase + "; sourceUID=" + activeDefinition
                    + "; type=" + originalType + "]: " + failure.Message, failure);
            }
            finally
            {
                LogPhase("definition_import_total", totalTimer.Elapsed.TotalMilliseconds, importSucceeded,
                    new { RecipeType = originalType, Definitions = definitionCount, RequestedCopies = copyCount, Mapped = mappedCount });
            }        }

        private static bool HasHistoricalDefinitionProof(Element historical, Element actual, string sourceUid, string version)
        {
            if (historical == null || actual == null || string.IsNullOrEmpty(sourceUid) || string.IsNullOrEmpty(version)
                || historical.GetType() != actual.GetType()
                || ElementHistoryNativeArchive.ReadHistoricalTypeVersion(actual) != version
                || ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(actual) != sourceUid
                || !(GetOrigins(actual) ?? new List<string>()).Contains(sourceUid)) return false;
            if (historical is FamilySymbol expected)
            {
                var symbol = (FamilySymbol)actual;
                return HasHistoricalDefinitionProof(expected.Family, symbol.Family,
                    ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(expected.Family), version);
            }
            return true;
        }

        private sealed class RejectHistoricalFamilyOverwrite : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            { overwriteParameterValues = false; return false; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Project; overwriteParameterValues = false; return false; }
        }

        private static void ImportNewHistoricalFamily(Document source, Document destination, Family historical,
            List<FamilySymbol> historicalSymbols, string version, HashSet<string> existingFamilies, HashSet<string> existingDefinitions)
        {
            if (historical.IsInPlace || !historical.IsEditable || historicalSymbols.Count == 0)
                throw new InvalidOperationException("FrozenFamilyLoad: la famille historique ne peut pas etre chargee independamment.");
            if (historical.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("FrozenFamilyLoad: le nom historique ne peut pas etre conserve dans un fichier famille.");
            if (new FilteredElementCollector(destination).OfClass(typeof(Family)).Cast<Family>()
                .Any(family => string.Equals(family.Name, historical.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("FrozenFamilyLoad: une famille homonyme non prouvee existe deja.");
            var directory = Path.Combine(Path.GetTempPath(), "BIMaestro", "history-family-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, historical.Name + ".rfa");
            Document familyDocument = null;
            try
            {
                // EditFamily is an independent copy. Saving under the historical
                // versioned name makes a new loadable family instead of pasting
                // symbols into the user's original family container.
                using (ElementHistoryNativeArchive.BeginHistoricalScope(source.Application))
                    familyDocument = source.EditFamily(historical);
                if (ElementHistoryNativeArchive.SnapshotExternalModelIds(familyDocument).Count > 0)
                    throw new InvalidOperationException("La famille historique contient un objet de maquette liee ou importee exclu de la restauration.");
                using (var options = new SaveAsOptions { Compact = true, MaximumBackups = 1, OverwriteExistingFile = false })
                    familyDocument.SaveAs(path, options);
                if (!familyDocument.Close(false)) throw new InvalidOperationException("FrozenFamilyLoad: fermeture de la famille temporaire refusee.");
                familyDocument = null;
                using (var transaction = new Transaction(destination, "BIMaestro - Charger famille historique independante"))
                {
                    transaction.Start();
                    var externalBefore = ElementHistoryNativeArchive.SnapshotExternalModelIds(destination);
                    if (!destination.LoadFamily(path, new RejectHistoricalFamilyOverwrite(), out var loaded) || loaded == null)
                        throw new InvalidOperationException("FrozenFamilyLoad: Revit a refuse le chargement sans ecrasement.");
                    ElementHistoryNativeArchive.ValidateNoExternalModelsAdded(destination, externalBefore);
                    StampNewHistoricalFamily(historical, loaded, historicalSymbols, destination, version, existingFamilies, existingDefinitions);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("FrozenFamilyLoad: chargement historique non valide.");
                }
            }
            finally
            {
                if (familyDocument != null)
                    try { if (!familyDocument.IsValidObject || familyDocument.Close(false)) familyDocument = null; } catch { }
                if (familyDocument == null)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    try { var backup = Path.ChangeExtension(path, ".0001.rfa"); if (File.Exists(backup)) File.Delete(backup); } catch { }
                    try { Directory.Delete(directory, false); } catch { }
                }
            }
        }

        private static void StampNewHistoricalFamily(Family historical, Family loaded, List<FamilySymbol> historicalSymbols,
            Document destination, string version, HashSet<string> existingFamilies, HashSet<string> existingDefinitions)
        {
            if (ElementHistoryNativeArchive.ReadHistoricalTypeVersion(historical) != version
                || string.IsNullOrEmpty(ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(historical))
                || loaded == null || existingFamilies.Contains(loaded.UniqueId) || loaded.Name != historical.Name
                || loaded.IsInPlace || loaded.FamilyCategory?.Id != historical.FamilyCategory?.Id
                || loaded.FamilyPlacementType != historical.FamilyPlacementType
                || loaded.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger()
                    != historical.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger())
                throw new InvalidOperationException("FrozenFamilyLoad: Revit n’a pas cree une nouvelle famille historique independante.");
            var symbols = loaded.GetFamilySymbolIds().Select(destination.GetElement).OfType<FamilySymbol>().ToList();
            var pairs = new List<KeyValuePair<FamilySymbol, FamilySymbol>>();
            foreach (var expected in historicalSymbols)
            {
                if (ElementHistoryNativeArchive.ReadHistoricalTypeVersion(expected) != version
                    || string.IsNullOrEmpty(ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(expected)))
                    throw new InvalidOperationException("FrozenFamilyLoad: symbole historique sans provenance.");
                var candidates = symbols.Where(symbol => symbol.GetType() == expected.GetType() && symbol.Name == expected.Name).ToList();
                if (candidates.Count != 1 || existingDefinitions.Contains(candidates[0].UniqueId)
                    || candidates[0].Family.Id != loaded.Id)
                    throw new InvalidOperationException("FrozenFamilyLoad: le symbole charge ne provient pas de la nouvelle famille.");
                pairs.Add(new KeyValuePair<FamilySymbol, FamilySymbol>(expected, candidates[0]));
            }
            // Only objects created by the direct load above receive proof from
            // the exact immutable source. Existing user families are never stamped.
            SetOrigins(loaded, GetOrigins(historical));
            foreach (var pair in pairs) SetOrigins(pair.Value, GetOrigins(pair.Key));
        }

        private static void RemapDurableDefinitionReferences(HistoryRecipe recipe, Dictionary<string, string> remap)
        {
            string Map(string uid) => uid != null && remap.TryGetValue(uid, out var actual) ? actual : uid;
            recipe.Type = Map(recipe.Type); recipe.Level = Map(recipe.Level);
            if (recipe.HistoricalPlacement != null)
            {
                recipe.HistoricalPlacement.LevelSourceUniqueId = Map(recipe.HistoricalPlacement.LevelSourceUniqueId);
                recipe.HistoricalPlacement.HostSourceUniqueId = Map(recipe.HistoricalPlacement.HostSourceUniqueId);
            }
            if (recipe.Network != null) recipe.Network.SystemType = Map(recipe.Network.SystemType);
            foreach (var parameter in (recipe.Parameters ?? new List<HistoryParameter>())
                .Concat(recipe.HistoricalPlacement?.ElevationParameters ?? new List<HistoryParameter>()))
            { parameter.Reference = Map(parameter.Reference); parameter.Definition = Map(parameter.Definition); }
            foreach (var port in (recipe.Ports ?? new List<HistoryPort>())
                .Concat((recipe.Connections ?? new List<HistoryConnection>()).SelectMany(connection => new[] { connection.Port, connection.PeerPort })))
                if (port != null) port.SystemType = Map(port.SystemType);
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>())
                if (circuit?.Recipe != null) RemapDurableDefinitionReferences(circuit.Recipe, remap);
        }

        private sealed class ReuseProvenDefinitionCollision : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }
        private sealed class RejectDefinitionCollision : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.Abort;
        }

        private static List<HistoryRestoreRequest> OrderByDependencies(List<HistoryRestoreRequest> selected)
        {
            var byId = selected.Where(r => !string.IsNullOrEmpty(r.SourceUniqueId)).ToDictionary(r => r.SourceUniqueId);
            var visited = new HashSet<HistoryRestoreRequest>();
            var ordered = new List<HistoryRestoreRequest>();
            void Visit(HistoryRestoreRequest request)
            {
                if (!visited.Add(request)) return;
                var references = new[] { request.Recipe?.Host, request.SuperComponentUniqueId, request.Recipe?.Native?.RootSourceUniqueId }
                    .Concat(request.Recipe?.SystemMembers ?? Enumerable.Empty<string>())
                    .Concat(request.Recipe?.Parameters?.Select(p => p.Reference) ?? Enumerable.Empty<string>());
                foreach (var reference in references.Where(r => !string.IsNullOrEmpty(r)))
                    if (byId.TryGetValue(reference, out var dependency)) Visit(dependency);
                ordered.Add(request);
            }
            // Sketch lines must follow their recorded owning roof/floor/ceiling,
            // even when the history lists the dependent deletions first.
            foreach (var request in selected.Where(r => r.Recipe != null && r.Recipe.SystemMembers == null)) Visit(request);
            foreach (var request in selected.Where(r => r.Recipe?.SystemMembers != null)) Visit(request);
            foreach (var request in selected) Visit(request);
            return ordered;
        }

        private static Dictionary<string, string> MarkSketchOrigins(Document doc, Element element, HistoryRecipe recipe)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (recipe.SketchCurves == null || recipe.SketchCurves.Count == 0) return result;
            var candidates = ElementHistoryReconstruction.SketchCurves(doc, element).ToList();
            bool SamePoint(XYZ a, double[] b) => recipe.Kind == "roof"
                ? Math.Abs(a.X - b[0]) < 1e-5 && Math.Abs(a.Y - b[1]) < 1e-5
                : a.DistanceTo(new XYZ(b[0], b[1], b[2])) < 1e-5;
            foreach (var saved in recipe.SketchCurves.Where(c => !string.IsNullOrEmpty(c.SourceUniqueId)))
            {
                var matches = candidates.Where(c =>
                {
                    var curve = c.GeometryCurve;
                    return ((SamePoint(curve.GetEndPoint(0), saved.Start) && SamePoint(curve.GetEndPoint(1), saved.End))
                        || (SamePoint(curve.GetEndPoint(1), saved.Start) && SamePoint(curve.GetEndPoint(0), saved.End)))
                        && (saved.Mid == null ? curve is Line : curve is Arc && SamePoint(curve.Evaluate(0.5, true), saved.Mid));
                }).ToList();
                if (matches.Count != 1) continue;
                var child = matches[0];
                var schema = OriginSchema();
                var entity = new Entity(schema);
                var origins = (saved.RestorationOrigins ?? new List<string>()).Concat(new[] { saved.SourceUniqueId }).Distinct().ToList();
                entity.Set<IList<string>>(schema.GetField("OriginalUniqueIds"), origins);
                child.SetEntity(entity);
                foreach (var origin in origins) result[origin] = child.UniqueId;
                candidates.Remove(child);
            }
            return result;
        }

        // Repaint in quick waves, without sleeps or pumping UI messages while
        // Revit is modifying the model. Expensive views automatically refresh
        // less often: target about 5% repaint overhead after the first refresh.
        private sealed class RestoreDisplay
        {
            private Action _refresh;
            private readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();
            private double _intervalMilliseconds = 120;
            private bool _hasRefreshed;
            private bool _dirty;

            internal RestoreDisplay(Action refresh) { _refresh = refresh; }

            internal void ElementCreated()
            {
                if (_refresh == null) return;
                _dirty = true;
                if (!_hasRefreshed || _sinceRefresh.Elapsed.TotalMilliseconds >= _intervalMilliseconds)
                    Flush();
            }

            internal void Flush(bool force = false)
            {
                if (_refresh == null || (!_dirty && !force)) return;
                var cost = Stopwatch.StartNew();
                try { _refresh(); }
                catch
                {
                    // Display failures must never cancel a valid restoration.
                    _refresh = null;
                    return;
                }
                _intervalMilliseconds = Math.Max(_intervalMilliseconds, cost.Elapsed.TotalMilliseconds * 20);
                _hasRefreshed = true;
                _dirty = false;
                _sinceRefresh.Restart();
            }
        }

        private static void RepairRestoredFittings(Document doc, List<HistoryRestoreRequest> selected,
            Dictionary<string, string> index, HistoryRestoreBatch batch)
        {
            foreach (var request in selected.Where(r => r.Recipe?.Kind == "family"))
            {
                var result = batch.Items.FirstOrDefault(i => i.SourceUniqueId == request.SourceUniqueId);
                if (result?.Existing != true || result.UniqueId == request.SourceUniqueId) continue;
                var instance = ElementHistoryReconstruction.FindOriginal(doc, result.UniqueId) as FamilyInstance;
                if (instance == null || !(GetOrigins(instance)?.Contains(request.SourceUniqueId) == true)) continue;
                var saved = request.Recipe.Ports ?? request.Recipe.Connections?.Select(c => c.Port).ToList();
                if (saved == null || saved.Count == 0 || saved.All(p => ElementHistoryNetwork.Match(instance, p) != null)) continue;
                using (var tx = new Transaction(doc, "BIMaestro - Réparer dimensions raccord restauré"))
                {
                    tx.Start();
                    var failures = new RestoreFailures();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    try
                    {
                        // Only our previously recreated, unmoved instances are eligible.
                        // Never resize an original object or disconnect an unrelated neighbor.
                        if (!(instance.Location is LocationPoint point) || point.Point.DistanceTo(ElementHistoryNetwork.Vector(request.Recipe.Point)) > 1e-4
                            || request.Recipe.BasisX == null || request.Recipe.BasisZ == null
                            || instance.GetTransform().BasisX.DistanceTo(ElementHistoryNetwork.Vector(request.Recipe.BasisX)) > 1e-4
                            || instance.GetTransform().BasisZ.DistanceTo(ElementHistoryNetwork.Vector(request.Recipe.BasisZ)) > 1e-4)
                            throw new InvalidOperationException("Raccord déplacé, réorienté ou orientation historique absente : conservé sans modification.");
                        var peers = new HashSet<string>((request.Recipe.Connections ?? new List<HistoryConnection>())
                            .Select(c => Resolve(doc, index, c.Peer)).Where(id => id != null));
                        foreach (var port in ElementHistoryNetwork.Ports(instance))
                            foreach (var peer in port.AllRefs.Cast<Connector>().Where(c => c.Owner.Id != instance.Id && !(c.Owner is MEPSystem)
                                && (c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Curve || c.ConnectorType == ConnectorType.Physical)).ToList())
                            {
                                if (!port.IsConnectedTo(peer)) continue;
                                if (!peers.Contains(peer.Owner.UniqueId)) throw new InvalidOperationException("Une nouvelle connexion non historique existe : raccord conservé sans modification.");
                                port.DisconnectFrom(peer);
                            }
                        var origins = GetOrigins(instance);
                        var previousUid = instance.UniqueId;
                        try { ElementHistoryNetwork.RestoreFamilyPorts(doc, instance, request.Recipe); }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (InvalidOperationException)
                        { instance = ElementHistoryNetwork.RebuildWithSizingStubs(doc, instance, request.Recipe, true); }
                        if (instance.UniqueId != previousUid && !origins.Contains(previousUid)) origins.Add(previousUid);
                        var schema = OriginSchema();
                        var entity = new Entity(schema);
                        entity.Set<IList<string>>(schema.GetField("OriginalUniqueIds"), origins);
                        instance.SetEntity(entity);
                        var repairedUid = instance.UniqueId;
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(failures.Message ?? "Réparation refusée.");
                        result.UniqueId = repairedUid;
                        foreach (var origin in origins) index[origin] = repairedUid;
                        result.Repaired = true;
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Exception ex)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        batch.RepairFailures.Add(request.Label + " : " + ex.Message);
                    }
                }
            }
        }

        private static void Reconnect(Document doc, List<HistoryRestoreRequest> selected,
            Dictionary<string, string> index, HistoryRestoreBatch batch)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var recipes = selected.Where(r => r.SourceUniqueId != null).GroupBy(r => r.SourceUniqueId)
                .ToDictionary(g => g.Key, g => g.First().Recipe, StringComparer.Ordinal);
            foreach (var request in selected)
            {
                var uid = Resolve(doc, index, request.SourceUniqueId);
                if (uid == null) continue;
                foreach (var link in request.Recipe?.Connections ?? new List<HistoryConnection>())
                {
                    var sourceMember = link.SourceMember ?? request.SourceUniqueId;
                    uid = Resolve(doc,index,sourceMember);
                    if (uid == null) { batch.ConnectionFailures.Add(request.Label + " : composant source absent."); continue; }
                    // Both ends save the same edge. Include ports to retain multiple links between owners.
                    string aKey = sourceMember + ":" + Newtonsoft.Json.JsonConvert.SerializeObject(link.Port?.Point);
                    string bKey = link.Peer + ":" + Newtonsoft.Json.JsonConvert.SerializeObject(link.PeerPort?.Point);
                    string key = string.CompareOrdinal(aKey, bKey) < 0 ? aKey + "|" + bKey : bKey + "|" + aKey;
                    if (!visited.Add(key)) continue;
                    string label = request.Label + " → " + link.Peer;
                    var peerUid = Resolve(doc, index, link.Peer);
                    if (peerUid == null) { batch.ConnectionFailures.Add(label + " : élément voisin absent."); continue; }
                    using (var connectionGroup = new TransactionGroup(doc, "BIMaestro - Valider connexion historique"))
                    {
                        connectionGroup.Start();
                        using (var tx = new Transaction(doc, "BIMaestro - Rétablir connexion historique"))
                        {
                            tx.Start();
                            var failures = new RestoreFailures();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                            try
                            {
                                var a = ElementHistoryNetwork.Match(doc.GetElement(uid), link.Port);
                                var b = ElementHistoryNetwork.Match(doc.GetElement(peerUid), link.PeerPort);
                                if (a == null || b == null) throw new InvalidOperationException("Connecteur déplacé ou introuvable : "
                                    + (a == null ? "source [" + ElementHistoryNetwork.DescribeMismatch(doc.GetElement(uid), link.Port) + "] " : "")
                                    + (b == null ? "voisin [" + ElementHistoryNetwork.DescribeMismatch(doc.GetElement(peerUid), link.PeerPort) + "]" : ""));
                                recipes.TryGetValue(link.Peer ?? "", out var peerRecipe);
                                recipes.TryGetValue(sourceMember ?? "", out var memberRecipe);
                                var connectionGuard = ElementHistoryNetwork.CaptureConnectionGuard(doc,
                                    doc.GetElement(uid), doc.GetElement(peerUid), memberRecipe, peerRecipe,
                                    link.Port, link.PeerPort);
                                if (a.IsConnectedTo(b))
                                {
                                    ElementHistoryNetwork.VerifyConnectionGuard(doc, connectionGuard);
                                    tx.RollBack(); batch.ConnectionsExisting++; continue;
                                }
                                if (a.IsConnected || b.IsConnected) throw new InvalidOperationException("Connecteur déjà utilisé par une autre connexion.");
                                if (a.Origin.DistanceTo(b.Origin) > 1e-4) throw new InvalidOperationException("Les connecteurs ne coïncident plus.");
                                a.ConnectTo(b);
                                doc.Regenerate();
                                ElementHistoryNetwork.VerifyConnectionGuard(doc, connectionGuard, verifyHistoricalEndpoints: false);
                                // A direct historical edge must not silently become an extra fitting.
                                a = ElementHistoryNetwork.Match(doc.GetElement(uid), link.Port);
                                b = ElementHistoryNetwork.Match(doc.GetElement(peerUid), link.PeerPort);
                                if (a == null || b == null || !a.IsConnectedTo(b)) throw new InvalidOperationException("Connexion directe non rétablie.");
                                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(failures.Message ?? "Connexion refusée par Revit.");
                                ElementHistoryNetwork.VerifyConnectionGuard(doc, connectionGuard);
                                a = ElementHistoryNetwork.Match(ElementHistoryReconstruction.FindOriginal(doc, uid), link.Port);
                                b = ElementHistoryNetwork.Match(ElementHistoryReconstruction.FindOriginal(doc, peerUid), link.PeerPort);
                                if (a == null || b == null || !a.IsConnectedTo(b))
                                    throw new InvalidOperationException("Connexion supprimée par Revit lors de la validation.");
                                if (connectionGroup.Assimilate() != TransactionStatus.Committed)
                                    throw new InvalidOperationException("Connexion non validée.");
                                batch.ConnectionsRestored++;
                            }
                            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                            catch (Exception ex)
                            {
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                                batch.ConnectionFailures.Add(label + " : " + ex.Message);
                            }
                        }
                    }
                }
            }
        }

        private sealed class RestoreFailures : IFailuresPreprocessor
        {
            public string Message { get; private set; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages = accessor.GetFailureMessages();
                var errors = messages.Where(m => m.GetSeverity() != FailureSeverity.Warning).ToList();
                if (errors.Count > 0)
                {
                    Message = string.Join("\n", errors.Select(m => m.GetDescriptionText()));
                    return FailureProcessingResult.ProceedWithRollBack;
                }
                // Return transaction warnings to the caller instead of blocking the
                // batch with a modal; errors always roll back the affected operation.
                Message = string.Join("\n", messages.Select(m => m.GetDescriptionText()));
                foreach (var warning in messages) accessor.DeleteWarning(warning);
                return FailureProcessingResult.Continue;
            }
        }
    }
}

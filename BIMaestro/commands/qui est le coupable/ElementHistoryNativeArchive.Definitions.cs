using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        // The caller proves that every referenced definition still has the
        // revision captured with this recipe. Geometry is never read again.
        internal static Dictionary<string, HistoryRecipe> ExportDurableDefinitions(Document source,
            IEnumerable<HistoryRestoreRequest> requests, string outputRvt, string sourceVersion,
            Func<string, bool> definitionUnchanged, Dictionary<string, string> failures = null)
        {
            if (source == null || !source.IsValidObject || source.IsFamilyDocument || source.IsModifiable)
                throw new InvalidOperationException("FrozenDefinitions: un projet vivant hors transaction est requis.");
            if (definitionUnchanged == null) throw new ArgumentNullException(nameof(definitionUnchanged));
            if (string.IsNullOrEmpty(sourceVersion) || sourceVersion.Length < 12 || sourceVersion.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("La version de definitions est absente ou invalide.", nameof(sourceVersion));
            if (string.IsNullOrEmpty(outputRvt) || !Path.IsPathRooted(outputRvt) || Path.GetExtension(outputRvt).ToLowerInvariant() != ".rvt")
                throw new ArgumentException("Un nouveau chemin RVT absolu est requis.", nameof(outputRvt));
            outputRvt = Path.GetFullPath(outputRvt);
            if (File.Exists(outputRvt)) throw new IOException("La capsule existe deja.");
            var input = (requests ?? Enumerable.Empty<HistoryRestoreRequest>()).Take(33).ToList();
            if (input.Count > 32) throw new InvalidOperationException("FrozenDefinitions: un lot est limite a 32 recettes.");
            var result = new Dictionary<string, HistoryRecipe>(StringComparer.Ordinal);
            var required = new Dictionary<string, Element>(StringComparer.Ordinal);
            var contexts = new Dictionary<string, List<HistoryNativeContextDefinition>>(StringComparer.Ordinal);
            foreach (var request in input)
            {
                try
                {
                    if (string.IsNullOrEmpty(request?.SourceUniqueId) || request.Recipe == null || request.Recipe.Native != null
                        || request.Recipe.Kind == "native_archive" || request.Recipe.OpeningFallback != null)
                        throw new InvalidOperationException("FrozenDefinitions: recette directe autonome attendue.");
                    if (!(source.GetElement(request.Recipe.Type ?? "") is ElementType))
                        throw new InvalidOperationException("FrozenDefinitions: type principal absent ou non pris en charge.");
                    var recipe = JObject.FromObject(request.Recipe).ToObject<HistoryRecipe>();
                    var definitions = FrozenDefinitionClosure(source, recipe, definitionUnchanged, out var projectContext);
                    result.Add(request.SourceUniqueId, recipe);
                    contexts.Add(request.SourceUniqueId, projectContext);
                    foreach (var definition in definitions) required[definition.UniqueId] = definition;
                }
                catch (Exception ex) when (failures != null && !string.IsNullOrEmpty(request?.SourceUniqueId))
                { failures[request.SourceUniqueId] = ex.ToString(); }
            }
            if (result.Count == 0) return result;
            Directory.CreateDirectory(Path.GetDirectoryName(outputRvt));
            Document capsule = null;
            var stage = "create_definition_capsule";
            try
            {
                using (BeginHistoricalScope(source.Application)) capsule = source.Application.NewProjectDocument(UnitSystem.Metric);
                OwnedArchives.Add(capsule);
                var templateTypes = new HashSet<ElementId>(new FilteredElementCollector(capsule).WhereElementIsElementType().ToElementIds());
                var templateDefinitions = new HashSet<ElementId>(templateTypes.Concat(new FilteredElementCollector(capsule).OfClass(typeof(Material)).ToElementIds()));
                var renamed = new HashSet<ElementId>();
                Dictionary<string, Element> mapped;
                using (var tx = new Transaction(capsule, "BIMaestro - Figer definitions de recette"))
                {
                    tx.Start(); ConfigureDurableFailures(tx);
                    stage = "rename_definition_template";
                    foreach (var id in templateTypes)
                    {
                        var type = capsule.GetElement(id) as ElementType;
                        if (IsPresentationDefinition(type) || type?.CanBeRenamed != true) continue;
                        try { type.Name = VersionedDefinitionName(type.Name, sourceVersion, "template:" + DurableTypeNameKey(type)); renamed.Add(id); }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (Exception) { /* Unsupported unused stock types remain unversioned. */ }
                    }
                    foreach (var id in templateDefinitions.Except(templateTypes))
                    {
                        var material = capsule.GetElement(id) as Material;
                        material.Name = VersionedDefinitionName(material.Name, sourceVersion, "template-material:" + material.Name);
                    }
                    stage = "copy_frozen_definitions";
                    foreach (var uid in required.Keys)
                        if (!definitionUnchanged(uid)) throw new InvalidOperationException("FrozenDefinitionChanged: " + uid);
                    var ids = required.Values.Where(e => !(e is Family)).Select(e => e.Id).Distinct().ToList();
                    var copyInputs = required.Values.Select(e => new {
                        Id = e.Id.GetIdLongValue(), Uid = e.UniqueId, Class = e.GetType().FullName, Name = e.Name,
                        IncludedInCopy = !(e is Family), LevelProjectElevation = (e as Level)?.ProjectElevation
                    }).ToArray();
                    var copyRecipeIds = result.Keys.ToArray();
                    var copyTimer = System.Diagnostics.Stopwatch.StartNew();
                    HistoryPerformanceLog.RecordDetails(source, "definition_export_copy_inputs", 0, ids.Count, 0, true,
                        new { RecipeSourceIds = copyRecipeIds, Definitions = copyInputs });
                    try
                    {
                        CopyFrozenDefinitions(source, capsule, required.Values, copyRecipeIds,
                            (operation, identity) => stage = operation + ":" + identity);
                    }
                    catch (Exception copyFailure)
                    {
                        HistoryPerformanceLog.RecordDetails(source, "definition_export_copy", copyTimer.Elapsed.TotalMilliseconds, ids.Count, 0, false,
                            new { RecipeSourceIds = copyRecipeIds, Definitions = copyInputs, Failure = copyFailure.ToString() });
                        throw;
                    }
                    HistoryPerformanceLog.RecordDetails(source, "definition_export_copy", copyTimer.Elapsed.TotalMilliseconds, ids.Count, 0, true,
                        new { RecipeSourceIds = copyRecipeIds, Definitions = copyInputs });
                    capsule.Regenerate();
                    mapped = StampDurableDependencyOrigins(source, capsule, templateDefinitions, renamed, sourceVersion, result.Values,
                        (operation, identity) => stage = operation + ":" + identity);
                    foreach (var pair in mapped)
                        if (pair.Value is Level historicalLevel && (!(capsule.GetElement(pair.Key) is Level copiedLevel)
                            || Math.Abs(copiedLevel.ProjectElevation - historicalLevel.ProjectElevation) > 1e-8))
                            throw new InvalidOperationException("FrozenLevelElevationMismatch: " + pair.Value.UniqueId);
                    var preserved = new HashSet<string>(mapped.Values.Select(e => e.UniqueId), StringComparer.Ordinal);
                    foreach (var uid in required.Keys)
                        if (!preserved.Contains(uid)) throw new InvalidOperationException("FrozenDefinitionMissing: " + uid);
                    // Include definitions imported implicitly by Revit in the proof.
                    foreach (var definition in mapped.Values)
                        if (!definitionUnchanged(definition.UniqueId)) throw new InvalidOperationException("FrozenDefinitionChanged: " + definition.UniqueId);
                    stage = "version_frozen_definitions";
                    foreach (var pair in mapped)
                    {
                        var copy = capsule.GetElement(pair.Key); var original = pair.Value;
                        if (copy is Family family) family.Name = VersionedDefinitionName(original.Name, sourceVersion, original.UniqueId);
                        else if (copy is ElementType type)
                        {
                            if (!type.CanBeRenamed) throw new InvalidOperationException("FrozenDefinitionNotVersionable: " + original.UniqueId);
                            type.Name = VersionedDefinitionName(original.Name, sourceVersion, original.UniqueId);
                        }
                        else if (copy is Material material) material.Name = VersionedDefinitionName(original.Name, sourceVersion, original.UniqueId);
                        StampDurableDefinition(copy, sourceVersion);
                    }
                    stage = "preserve_frozen_type_identity";
                    RestoreFrozenTypeIdentities(capsule, mapped, templateDefinitions);
                    CommitDurableTransaction(tx);
                }
                ValidateFrozenTypeIdentities(capsule, mapped, templateDefinitions);
                foreach (var pair in result)
                {
                    var sourceType = pair.Value.Type;
                    var matches = mapped.Where(p => p.Value.UniqueId == sourceType).Select(p => p.Key).Distinct().ToList();
                    if (matches.Count != 1) throw new InvalidOperationException("FrozenDefinitionAmbiguous: " + sourceType);
                    pair.Value.OpeningFallback = new HistoryNativeArtifact
                    {
                        File = outputRvt, Ready = true, DefinitionOnly = true, TypeVersion = sourceVersion,
                        RootUniqueId = matches[0], RootSourceUniqueId = sourceType, MemberSourceUniqueId = sourceType,
                        ContextDefinitions = contexts[pair.Key], ReadyUtc = DateTime.UtcNow
                    };
                    StampDurablePortVersions(pair.Value, sourceVersion);
                }
                foreach (var pair in result)
                    FrozenDefinitionClosure(source, pair.Value, definitionUnchanged, out _);
                stage = "save_definition_capsule";
                RejectExternalModelsInCapsule(capsule);
                using (var options = new SaveAsOptions { Compact = true, MaximumBackups = 1, OverwriteExistingFile = false }) capsule.SaveAs(outputRvt, options);
                ValidateFrozenTypeIdentities(capsule, mapped, templateDefinitions);
                if (!capsule.Close(false)) throw new InvalidOperationException("La capsule de definitions ne peut pas etre fermee.");
                OwnedArchives.Remove(capsule); capsule = null;
                return result;
            }
            catch (Exception ex) { throw DurableExportDiagnostic(ex, stage, null, null, null, source, capsule); }
            finally
            {
                if (capsule != null)
                {
                    try { if (!capsule.IsValidObject || capsule.Close(false)) OwnedArchives.Remove(capsule);
                        else if (!RetiredArchives.Any(d => ReferenceEquals(d, capsule))) RetiredArchives.Add(capsule); }
                    catch { if (!RetiredArchives.Any(d => ReferenceEquals(d, capsule))) RetiredArchives.Add(capsule); }
                }
            }
        }

        // Revit can reject a mixed paste of levels, loadable symbols and MEP
        // definitions even when those definitions are individually transferable.
        // Copy context first, then system definitions, and finally physical
        // types. The source remains read-only and the enclosing transaction
        // still makes the complete closure atomic.
        private static void CopyFrozenDefinitions(Document source, Document capsule, IEnumerable<Element> definitions,
            string[] recipeIds, Action<string, string> report)
        {
            var ordered = definitions.Where(element => !(element is Family))
                .OrderBy(element => element is Level ? 0 : element is Material ? 1 : element is MEPSystemType ? 2 : 3)
                .ThenBy(element => element.UniqueId, StringComparer.Ordinal).ToList();
            foreach (var definition in ordered)
            {
                RejectExternalModel(definition, "frozen_definition_copy");
                report("copy_frozen_definition", definition.UniqueId + "|" + definition.GetType().FullName);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                using (var copy = new SubTransaction(capsule))
                {
                    copy.Start();
                    try
                    {
                        using (var options = Options())
                            ElementTransformUtils.CopyElements(source, new[] { definition.Id }, capsule, Transform.Identity, options);
                        capsule.Regenerate();
                        if (copy.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("FrozenDefinitionCopyCommit: " + definition.UniqueId);
                        HistoryPerformanceLog.RecordDetails(source, "definition_export_copy_definition", timer.Elapsed.TotalMilliseconds,
                            1, 0, true, new { RecipeSourceIds = recipeIds, SourceUid = definition.UniqueId,
                                Class = definition.GetType().FullName, Name = definition.Name });
                    }
                    catch (Exception failure)
                    {
                        if (copy.GetStatus() == TransactionStatus.Started) copy.RollBack();
                        HistoryPerformanceLog.RecordDetails(source, "definition_export_copy_definition", timer.Elapsed.TotalMilliseconds,
                            1, 0, false, new { RecipeSourceIds = recipeIds, SourceUid = definition.UniqueId,
                                Class = definition.GetType().FullName, Name = definition.Name, Failure = failure.ToString() });
                        throw;
                    }
                }
            }
        }

        internal static string ReadHistoricalDefinitionSource(Element definition)
        {
            var values = (ElementHistoryRestoration.GetOrigins(definition) ?? new List<string>())
                .Where(value => value.StartsWith(HistoricalDefinitionSourcePrefix, StringComparison.Ordinal))
                .Select(value => value.Substring(HistoricalDefinitionSourcePrefix.Length)).Distinct().ToList();
            if (values.Count == 0) return null;
            if (values.Count != 1) throw new InvalidOperationException("FrozenDefinitionSourceAmbiguous: " + definition.UniqueId);
            return values[0];
        }

        internal static void ValidateHistoricalContext(Document destination, IEnumerable<HistoryNativeContextDefinition> contexts)
        {
            foreach (var context in contexts ?? Enumerable.Empty<HistoryNativeContextDefinition>())
            {
                var element = destination.GetElement(context.SourceUniqueId);
                var phases = destination.Phases.Cast<Phase>().ToList();
                if (!(element is Phase phase) || element.GetType().FullName != context.Class || element.Name != context.Name
                    || context.Ordinal < 0 || context.Ordinal >= phases.Count || phases[context.Ordinal].Id != phase.Id)
                    throw new InvalidOperationException("FrozenProjectContextChanged: " + context.SourceUniqueId);
            }
        }
        private static IEnumerable<HistoryRecipe> FrozenRecipes(HistoryRecipe recipe)
        {
            yield return recipe;
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>())
                if (circuit?.Recipe != null)
                    foreach (var nested in FrozenRecipes(circuit.Recipe)) yield return nested;
        }

        // Only definition dependencies belong in this identity. In particular,
        // moving an instance must not require another copy of its unchanged
        // family. Keep the roles aligned with FrozenDefinitionClosure below.
        internal static string DefinitionDependencyIdentity(HistoryRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            void Add(string role, string uid) { if (!string.IsNullOrEmpty(uid)) dependencies.Add(role + ":" + uid); }
            foreach (var nested in FrozenRecipes(recipe))
            {
                Add("kind", nested.Kind); Add("type", nested.Type); Add("level", nested.Level);
                Add("system", nested.Network?.SystemType);
                Add("placement-level", nested.HistoricalPlacement?.LevelSourceUniqueId);
                // A placement host may be a Level; retaining other host IDs is
                // conservative and never turns a physical host into a definition.
                Add("placement-host", nested.HistoricalPlacement?.HostSourceUniqueId);
                foreach (var parameter in (nested.Parameters ?? new List<HistoryParameter>())
                    .Concat(nested.HistoricalPlacement?.ElevationParameters ?? new List<HistoryParameter>()))
                { Add("parameter-definition", parameter.Definition); Add("parameter-reference", parameter.Reference); }
                foreach (var port in (nested.Ports ?? new List<HistoryPort>()).Concat((nested.Connections ?? new List<HistoryConnection>())
                    .SelectMany(connection => new[] { connection.Port, connection.PeerPort })))
                    if (port != null) Add("system", port.SystemType);
            }
            return new JObject { ["PrimaryType"] = recipe.Type,
                ["Dependencies"] = new JArray(dependencies.OrderBy(value => value, StringComparer.Ordinal)) }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static HistoryRecipe ApplyFrozenDefinitionArtifact(HistoryRecipe recipe, HistoryNativeArtifact artifact)
        {
            if (recipe == null || artifact == null || !artifact.DefinitionOnly || artifact.WholeModel
                || string.IsNullOrEmpty(artifact.PayloadId) || !string.IsNullOrEmpty(artifact.File)
                || artifact.RootSourceUniqueId != recipe.Type || string.IsNullOrEmpty(artifact.TypeVersion))
                throw new InvalidOperationException("FrozenDefinitionReuse: capsule de definitions incompatible.");
            var copy = JObject.FromObject(recipe).ToObject<HistoryRecipe>();
            copy.OpeningFallback = JObject.FromObject(artifact).ToObject<HistoryNativeArtifact>();
            StampDurablePortVersions(copy, artifact.TypeVersion);
            return copy;
        }

        private static List<Element> FrozenDefinitionClosure(Document source, HistoryRecipe recipe,
            Func<string, bool> unchanged, out List<HistoryNativeContextDefinition> contexts)
        {
            var capturedContexts = new List<HistoryNativeContextDefinition>();
            contexts = capturedContexts;
            var required = new Dictionary<string, Element>(StringComparer.Ordinal);
            var contextUids = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<Element>();
            void Add(string uid, bool parameterDefinition = false)
            {
                if (string.IsNullOrEmpty(uid) || required.ContainsKey(uid) || contextUids.Contains(uid)) return;
                var definition = source.GetElement(uid);
                if (definition == null) throw new InvalidOperationException("FrozenDefinitionAbsent: " + uid);
                RejectExternalModel(definition, "frozen_definition_closure");
                if (IsPresentationDefinition(definition)) return;
                if (!unchanged(uid)) throw new InvalidOperationException("FrozenDefinitionChanged: " + uid);
                if (definition is Phase phase)
                {
                    var phases = source.Phases.Cast<Phase>().ToList();
                    capturedContexts.Add(new HistoryNativeContextDefinition { SourceUniqueId = uid, Class = phase.GetType().FullName,
                        Name = phase.Name, Ordinal = phases.FindIndex(p => p.Id == phase.Id) });
                    contextUids.Add(uid); return;
                }
                if (parameterDefinition || definition is ParameterElement)
                    throw new InvalidOperationException("UnsupportedFrozenParameterBinding: " + uid);
                if (!(definition is ElementType) && !(definition is Family) && !(definition is Level) && !(definition is Material))
                    throw new InvalidOperationException("UnsupportedFrozenDependency: " + uid + "/" + definition.GetType().Name);
                if (required.Count >= 2048) throw new InvalidOperationException("FrozenDefinitions: fermeture de plus de 2048 definitions.");
                required.Add(uid, definition); pending.Enqueue(definition);
            }
            foreach (var nested in FrozenRecipes(recipe))
            {
                if (nested.Native != null || nested.OpeningFallback != null && !nested.OpeningFallback.DefinitionOnly)
                    throw new InvalidOperationException("FrozenDefinitions: une recette imbriquee depend encore d’une archive objet.");
                Add(nested.Type); Add(nested.Level); Add(nested.Network?.SystemType);
                Add(nested.HistoricalPlacement?.LevelSourceUniqueId);
                if (!string.IsNullOrEmpty(nested.HistoricalPlacement?.HostSourceUniqueId)
                    && source.GetElement(nested.HistoricalPlacement.HostSourceUniqueId) is Level hostLevel)
                    Add(hostLevel.UniqueId);
                foreach (var parameter in (nested.Parameters ?? new List<HistoryParameter>())
                    .Concat(nested.HistoricalPlacement?.ElevationParameters ?? new List<HistoryParameter>()))
                { Add(parameter.Definition, true); Add(parameter.Reference); }
                foreach (var port in (nested.Ports ?? new List<HistoryPort>()).Concat((nested.Connections ?? new List<HistoryConnection>()).SelectMany(c => new[] { c.Port, c.PeerPort })))
                    if (port != null) Add(port.SystemType);
            }
            while (pending.Count > 0)
            {
                var definition = pending.Dequeue();
                if (definition is FamilySymbol symbol) Add(symbol.Family.UniqueId);
                foreach (Parameter parameter in definition.Parameters)
                    if (parameter.HasValue && parameter.StorageType == StorageType.ElementId)
                    {
                        var referenced = source.GetElement(parameter.AsElementId());
                        if (referenced != null) Add(referenced.UniqueId);
                    }
            }
            return required.Values.ToList();
        }
    }
}

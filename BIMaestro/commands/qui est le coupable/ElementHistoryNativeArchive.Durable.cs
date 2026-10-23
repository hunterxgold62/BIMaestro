using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Analyse
{
    internal static partial class ElementHistoryNativeArchive
    {
        private sealed class DocumentReferenceComparer : IEqualityComparer<Document>
        {
            internal static readonly DocumentReferenceComparer Instance = new DocumentReferenceComparer();
            public bool Equals(Document first, Document second) => ReferenceEquals(first, second);
            public int GetHashCode(Document document) => RuntimeHelpers.GetHashCode(document);
        }

        private static bool SameLiveDocument(Document first, Document second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (ReferenceEquals(first, null) || ReferenceEquals(second, null)) return false;
            try { return first.IsValidObject && second.IsValidObject && first.Equals(second); }
            catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return false; }
        }

        private static bool ContainsLiveDocument(IEnumerable<Document> documents, Document document)
            => documents.Any(candidate => SameLiveDocument(candidate, document));

        private const string HistoricalTypeVersionPrefix = "BIMaestroHistoryDefinitionVersion:";
        private const string HistoricalDefinitionSourcePrefix = "BIMaestroHistoryDefinitionSource:";
        private static readonly Dictionary<Document, List<string>> HistoricalMaterializations
            = new Dictionary<Document, List<string>>(DocumentReferenceComparer.Instance);

        private sealed class HistoricalScope : IDisposable
        {
            private readonly Application _application;
            private readonly HashSet<Document> _before;
            private readonly HashSet<Document> _previous;
            private bool _disposed;
            internal HistoricalScope(Application application)
            {
                _application = application;
                _before = new HashSet<Document>(application.Documents.Cast<Document>(), DocumentReferenceComparer.Instance);
                _previous = DocumentsBeforeHistoricalOpen;
                DocumentsBeforeHistoricalOpen = _previous ?? _before;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    foreach (var document in _application.Documents.Cast<Document>())
                        if (!ContainsLiveDocument(_before, document)) OwnedArchives.Add(document);
                }
                finally { DocumentsBeforeHistoricalOpen = _previous; }
            }
        }

        // Must surround the API call, since DocumentOpened fires before it returns.
        internal static IDisposable BeginHistoricalScope(Application application)
            => new HistoricalScope(application ?? throw new ArgumentNullException(nameof(application)));

        private static void DeferHistoricalMaterializations(Document document, IEnumerable<string> paths)
        {
            if (!HistoricalMaterializations.TryGetValue(document, out var deferred))
                HistoricalMaterializations.Add(document, deferred = new List<string>());
            deferred.AddRange(paths);
        }

        private static void ReleaseHistoricalMaterializations(Document document)
        {
            if (!HistoricalMaterializations.TryGetValue(document, out var paths)) return;
            // Remove one lease only after its release succeeds; a failed cleanup
            // remains retryable without releasing another reader's lease twice.
            while (paths.Count > 0)
            {
                ElementHistoryDurableStore.ReleaseMaterialized(paths[0]);
                paths.RemoveAt(0);
            }
            HistoricalMaterializations.Remove(document);
        }

        internal static string ReadHistoricalTypeVersion(Element type)
        {
            var versions = (type == null ? null : ElementHistoryRestoration.GetOrigins(type))
                ?.Where(value => value.StartsWith(HistoricalTypeVersionPrefix, StringComparison.Ordinal))
                .Select(value => value.Substring(HistoricalTypeVersionPrefix.Length)).Distinct().ToList();
            if (versions == null || versions.Count == 0) return null;
            if (versions.Count != 1)
                throw new InvalidOperationException("La définition historique porte plusieurs versions incompatibles.");
            return versions[0];
        }

        internal static bool IsPresentationDefinition(Element element)
        {
            if (element is Level || element is Material || element is MEPSystemType) return false;
            return element is View || element is DimensionType || element is LineAndTextAttrSymbol || element is ViewFamilyType
                || element?.Category?.CategoryType == CategoryType.Annotation
                || element is Family family && family.FamilyCategory?.CategoryType == CategoryType.Annotation;
        }
        // Versioned names prevent substitution by ordinary live definitions.
        // A residual duplicate is accepted only for this exact capsule version.
        internal static void ValidateDurableTypeCompatibility(Document capsule, Document destination, string expectedVersion)
        {
            if (string.IsNullOrEmpty(expectedVersion))
                throw new InvalidOperationException("La version des définitions de la capsule est absente.");
            var destinationTypes = new FilteredElementCollector(destination).WhereElementIsElementType().Cast<ElementType>()
                .GroupBy(type => DurableTypeNameKey(type), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var type in new FilteredElementCollector(capsule).WhereElementIsElementType().Cast<ElementType>())
            {
                if (IsPresentationDefinition(type)) continue; // Presentation data is outside physical restoration.
                // Stock template definitions may carry a version tag; only a proven source is historical.
                if (ReadHistoricalDefinitionSource(type) == null) continue;
                var version = ReadHistoricalTypeVersion(type);
                if (version != expectedVersion)
                    throw new InvalidOperationException("La version d’un type ne correspond pas à celle de la capsule.");
                if (destinationTypes.TryGetValue(DurableTypeNameKey(type), out var duplicates)
                    && duplicates.Any(candidate => !SameDurableDefinition(type, candidate, expectedVersion)))
                    throw new InvalidOperationException("Un type existant masquerait la définition historique : " + type.Name);
            }
            var families = new FilteredElementCollector(destination).OfClass(typeof(Family)).Cast<Family>()
                .GroupBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var family in new FilteredElementCollector(capsule).OfClass(typeof(Family)).Cast<Family>())
            {
                if (IsPresentationDefinition(family) || ReadHistoricalDefinitionSource(family) == null) continue;
                if (ReadHistoricalTypeVersion(family) != expectedVersion)
                    throw new InvalidOperationException("La famille ne porte pas la version de la capsule.");
                if (families.TryGetValue(family.Name, out var duplicates)
                    && duplicates.Any(candidate => !SameDurableDefinition(family, candidate, expectedVersion)))
                    throw new InvalidOperationException("Une famille existante masquerait la définition historique : " + family.Name);
            }
            var materials = new FilteredElementCollector(destination).OfClass(typeof(Material)).Cast<Material>()
                .GroupBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var material in new FilteredElementCollector(capsule).OfClass(typeof(Material)).Cast<Material>())
            {
                if (ReadHistoricalDefinitionSource(material) == null) continue;
                if (ReadHistoricalTypeVersion(material) != expectedVersion)
                    throw new InvalidOperationException("La version du materiau ne correspond pas a celle de la capsule.");
                if (materials.TryGetValue(material.Name, out var duplicates)
                    && duplicates.Any(candidate => !SameDurableDefinition(material, candidate, expectedVersion)))
                    throw new InvalidOperationException("Un materiau existant masquerait la definition historique : " + material.Name);
            }
        }

        private static bool SameDurableDefinition(Element historical, Element candidate, string version)
        {
            if (ReadHistoricalTypeVersion(candidate) != version) return false;
            var source = (ElementHistoryRestoration.GetOrigins(historical) ?? new List<string>())
                .Where(origin => origin.StartsWith(HistoricalDefinitionSourcePrefix, StringComparison.Ordinal)).Distinct().ToList();
            var actual = new HashSet<string>(ElementHistoryRestoration.GetOrigins(candidate) ?? new List<string>(), StringComparer.Ordinal);
            return source.Count == 1 && source.All(actual.Contains);
        }

        private static string DurableTypeNameKey(ElementType type)
            => type.GetType().FullName + "|" + type.FamilyName + "|" + type.Name;

        private sealed class DurableItem
        {
            internal HistoryRestoreRequest Request;
            internal Element Element;
            internal Element Root;
            internal HistoryRecipe DirectSystem;
            internal ElementType SystemDefinition;
        }

        // Called from a Revit API context. The source checkpoint is never edited.
        // Only requested roots, their native dependencies and explicit system
        // definitions enter a fresh capsule; the full model is never SaveAs'ed.
        internal static Dictionary<string, HistoryRecipe> ExportDurableBatch(Document source,
            IEnumerable<HistoryRestoreRequest> requests, string outputRvt, string sourceVersion = null,
            Dictionary<string, string> failures = null)
        {
            if (source == null || source.IsFamilyDocument || source.IsModifiable)
                throw new InvalidOperationException("La conversion exige un projet historique hors transaction.");
            if (string.IsNullOrWhiteSpace(outputRvt) || !Path.IsPathRooted(outputRvt)
                || !string.Equals(Path.GetExtension(outputRvt), ".rvt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("La capsule exige un nouveau chemin RVT absolu.", nameof(outputRvt));
            outputRvt = Path.GetFullPath(outputRvt);
            if (File.Exists(outputRvt)) throw new IOException("Le fichier de capsule existe déjà.");
            var items = (requests ?? Enumerable.Empty<HistoryRestoreRequest>()).Select(request => ResolveDurableItem(source, request))
                .GroupBy(item => item.Element.UniqueId, StringComparer.Ordinal).Select(group => group.First()).ToList();
            if (items.Count == 0) return new Dictionary<string, HistoryRecipe>(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(outputRvt));
            var version = sourceVersion ?? Document.GetDocumentVersion(source).VersionGUID.ToString("N");
            if (version.Length < 12 || version.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("La version du checkpoint doit être un identifiant hexadécimal d’au moins 12 caractères.", nameof(sourceVersion));
            var result = new Dictionary<string, HistoryRecipe>(StringComparer.Ordinal);
            Document capsule = null;
            bool saved = false;
            var stage = "create_capsule";
            string rootContext = null, memberContext = null, definitionContext = null;
            Action<string, string> report = (operation, identity) => { stage = operation; definitionContext = identity; };
            try
            {
                using (BeginHistoricalScope(source.Application))
                    capsule = source.Application.NewProjectDocument(UnitSystem.Metric);
                OwnedArchives.Add(capsule);
                var templateTypes = new HashSet<ElementId>(new FilteredElementCollector(capsule)
                    .WhereElementIsElementType().ToElementIds());
                var renamedTemplateIds = new HashSet<ElementId>();
                var templateDefinitions = new HashSet<ElementId>(templateTypes.Concat(
                    new FilteredElementCollector(capsule).OfClass(typeof(Material)).ToElementIds()));
                using (var tx = new Transaction(capsule, "BIMaestro - Préparer capsule durable"))
                {
                    stage = "initialize_capsule";
                    tx.Start();
                    foreach (var type in templateTypes.Select(capsule.GetElement).OfType<ElementType>())
                    {
                        stage = "initialize_template_type";
                        definitionContext = DurableDefinitionDiagnostic(type);
                        if (IsPresentationDefinition(type) || !type.CanBeRenamed) continue;
                        try
                        {
                            type.Name = VersionedDefinitionName(type.Name, version, "template:" + DurableTypeNameKey(type));
                            renamedTemplateIds.Add(type.Id);
                        }
                        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                        catch (Exception ex)
                        {
                            // Several stock electrical types advertise CanBeRenamed
                            // but reject Name.set. An unused stock definition must
                            // not prevent exporting unrelated physical objects.
                            System.Diagnostics.Trace.WriteLine("History stock type retained: " + definitionContext + " " + ex.ToString());
                        }
                    }
                    foreach (var material in templateDefinitions.Select(capsule.GetElement).OfType<Material>())
                    {
                        stage = "initialize_template_material";
                        definitionContext = DurableDefinitionDiagnostic(material);
                        material.Name = VersionedDefinitionName(material.Name, version, "template-material:" + material.Name);
                    }
                    CommitDurableTransaction(tx);
                }
                var roots = items.Where(item => item.DirectSystem == null).GroupBy(item => item.Root.UniqueId, StringComparer.Ordinal);
                foreach (var group in roots)
                {
                    try
                    {
                    var root = group.First().Root;
                    rootContext = root.UniqueId;
                    memberContext = string.Join(",", group.Select(item => item.Element.UniqueId));
                    stage = "copy_root";
                    Element copiedRoot;
                    string copiedRootUid;
                    Dictionary<string, Element> mapping;
                    using (var atomic = new TransactionGroup(capsule, "BIMaestro - Convertir ensemble historique"))
                    {
                        atomic.Start();
                        using (var tx = new Transaction(capsule, "BIMaestro - Copier ensemble historique"))
                        {
                            tx.Start();
                            ConfigureDurableFailures(tx);
                            using (var options = Options()) copiedRoot = CopyRoot(source, root, capsule, options);
                            if (copiedRoot == null) throw new InvalidOperationException("La racine de l’ensemble n’a pas été copiée.");
                            copiedRootUid = copiedRoot.UniqueId;
                            stage = "commit_root_copy";
                            CommitDurableTransaction(tx);
                        }
                        stage = "remove_insulation";
                        copiedRoot = RequireDurableElement(capsule, copiedRootUid);
                        copiedRoot = RemoveCalo(capsule, copiedRoot);
                        if (copiedRoot == null) throw new InvalidOperationException("L’ensemble ne contient aucun objet pris en charge.");
                        copiedRootUid = copiedRoot.UniqueId;
                        using (var tx = new Transaction(capsule, "BIMaestro - Conserver identités historiques"))
                        {
                            tx.Start();
                            ConfigureDurableFailures(tx);
                            stage = "match_aggregate";
                            copiedRoot = RequireDurableElement(capsule, copiedRootUid);
                            mapping = MatchAggregate(source, root, capsule, copiedRoot);
                            foreach (var item in group)
                                if (!mapping.ContainsKey(item.Element.UniqueId))
                                    throw new InvalidOperationException("Le membre demandé n’a pas été conservé dans la capsule : " + item.Element.UniqueId);
                            foreach (var pair in mapping)
                            {
                                stage = "stamp_member_and_type";
                                memberContext = pair.Key;
                                var original = source.GetElement(pair.Key);
                                ElementHistoryRestoration.SetOrigins(pair.Value,
                                    (ElementHistoryRestoration.GetOrigins(original) ?? new List<string>()).Concat(new[] { original.UniqueId }));
                                StampDurableDefinitionPair(source.GetElement(original.GetTypeId()),
                                    capsule.GetElement(pair.Value.GetTypeId()), version);
                            }
                            CommitDurableTransaction(tx);
                        }
                    foreach (var item in group)
                    {
                        stage = "capture_member_metadata";
                        memberContext = item.Element.UniqueId;
                        RequireDurableElement(capsule, copiedRootUid);
                        var artifact = DurableArtifact(item, outputRvt, copiedRootUid, version);
                        var recipe = Recipe(item.Element, artifact);
                        PreserveDurableLiveMetadata(item, recipe);
                        result.Add(item.Element.UniqueId, recipe);
                    }
                        stage = "assimilate_aggregate";
                        if (atomic.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("L’ensemble historique n’a pas été validé.");
                    }
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Exception ex) when (failures != null)
                    {
                        var diagnostic = DurableExportDiagnostic(ex, stage, rootContext, memberContext, definitionContext, source, capsule);
                        foreach (var item in group)
                        {
                            result.Remove(item.Element.UniqueId);
                            failures[item.Element.UniqueId] = diagnostic.ToString();
                        }
                        System.Diagnostics.Trace.WriteLine(diagnostic.ToString());
                    }
                }
                foreach (var item in items.Where(item => item.DirectSystem != null))
                {
                    stage = "capture_logical_system";
                    memberContext = item.Element.UniqueId;
                    var recipe = item.DirectSystem;
                    if (item.SystemDefinition != null)
                    {
                        ElementType copiedType;
                        string copiedTypeUid;
                        using (var tx = new Transaction(capsule, "BIMaestro - Conserver définition système"))
                        {
                            tx.Start();
                            ConfigureDurableFailures(tx);
                            stage = "copy_system_definition";
                            definitionContext = item.SystemDefinition.UniqueId;
                            using (var options = Options())
                                ElementTransformUtils.CopyElements(source, new[] { item.SystemDefinition.Id }, capsule, Transform.Identity, options);
                            var matches = new FilteredElementCollector(capsule).WhereElementIsElementType().Cast<ElementType>()
                                .Where(type => type.GetType() == item.SystemDefinition.GetType() && type.Name == item.SystemDefinition.Name
                                    && type.FamilyName == item.SystemDefinition.FamilyName).ToList();
                            if (matches.Count != 1) throw new InvalidOperationException("L’identité du type de système copié est ambiguë.");
                            copiedType = matches[0];
                            copiedTypeUid = copiedType.UniqueId;
                            StampDurableDefinitionPair(item.SystemDefinition, copiedType, version);
                            CommitDurableTransaction(tx);
                        }
                        recipe.OpeningFallback = new HistoryNativeArtifact
                        {
                            File = outputRvt, Ready = true, DefinitionOnly = true, TypeVersion = version,
                            RootUniqueId = copiedTypeUid, RootSourceUniqueId = item.SystemDefinition.UniqueId,
                            MemberSourceUniqueId = item.SystemDefinition.UniqueId
                        };
                    }
                    PreserveDurableLiveMetadata(item, recipe);
                    result.Add(item.Element.UniqueId, recipe);
                }
                if (result.Count == 0) return result;
                using (var tx = new Transaction(capsule, "BIMaestro - Versionner définitions historiques"))
                {
                    tx.Start();
                    ConfigureDurableFailures(tx);
                    stage = "dependency_origins";
                    var sourceDefinitions = StampDurableDependencyOrigins(source, capsule, templateDefinitions, renamedTemplateIds, version, result.Values, report);
                    foreach (var id in new FilteredElementCollector(capsule).OfClass(typeof(Family)).ToElementIds())
                    {
                        var family = RequireDurableElement(capsule, id) as Family;
                        if (IsPresentationDefinition(family)) continue;
                        stage = "version_family";
                        definitionContext = DurableDefinitionDiagnostic(family);
                        sourceDefinitions.TryGetValue(family.UniqueId, out var original);
                        family.Name = VersionedDefinitionName(original?.Name ?? family.Name, version, original?.UniqueId ?? family.UniqueId);
                        StampDurableDefinition(family, version);
                    }
                    foreach (var id in new FilteredElementCollector(capsule).WhereElementIsElementType().ToElementIds())
                    {
                        var type = RequireDurableElement(capsule, id) as ElementType;
                        if (IsPresentationDefinition(type)) continue;
                        stage = "version_type";
                        definitionContext = DurableDefinitionDiagnostic(type);
                        if (!templateTypes.Contains(type.Id))
                        {
                            if (!type.CanBeRenamed)
                                throw new InvalidOperationException("La définition copiée ne peut pas être versionnée : " + type.Name);
                            sourceDefinitions.TryGetValue(type.UniqueId, out var original);
                            type.Name = VersionedDefinitionName(original?.Name ?? type.Name, version, original?.UniqueId ?? type.UniqueId);
                        }
                        if (renamedTemplateIds.Contains(type.Id) || !templateTypes.Contains(type.Id)) StampDurableDefinition(type, version);
                    }
                    foreach (var id in new FilteredElementCollector(capsule).OfClass(typeof(Material)).ToElementIds())
                    {
                        var material = RequireDurableElement(capsule, id) as Material;
                        if (!templateDefinitions.Contains(material.Id))
                        {
                            stage = "version_material";
                            definitionContext = DurableDefinitionDiagnostic(material);
                            sourceDefinitions.TryGetValue(material.UniqueId, out var original);
                            material.Name = VersionedDefinitionName(original?.Name ?? material.Name, version, original?.UniqueId ?? material.UniqueId);
                            StampDurableDefinition(material, version);
                        }
                    }
                    stage = "commit_definition_versions";
                    CommitDurableTransaction(tx);
                }
                // Detach metadata before annotating it: several live snapshots may
                // share their connection/placement DTOs with the input requests.
                foreach (var uid in result.Keys.ToList())
                {
                    stage = "serialize_recipe";
                    memberContext = uid;
                    result[uid] = Newtonsoft.Json.Linq.JObject.FromObject(result[uid]).ToObject<HistoryRecipe>();
                    StampDurablePortVersions(result[uid], version);
                }
                // SaveAs is outside every API transaction/group. Compact removes
                // dead data; it never serves as a substitute for selecting roots.
                using (var options = new SaveAsOptions { Compact = true, MaximumBackups = 1, OverwriteExistingFile = false })
                {
                    stage = "save_capsule";
                    capsule.SaveAs(outputRvt, options);
                }
                saved = true;
                stage = "close_capsule";
                if (!capsule.Close(false)) throw new InvalidOperationException("La capsule n’a pas pu être fermée après son enregistrement.");
                OwnedArchives.Remove(capsule);
                capsule = null;
                return result;
            }
            catch (Exception ex)
            {
                var diagnostic = DurableExportDiagnostic(ex, stage, rootContext, memberContext, definitionContext, source, capsule);
                System.Diagnostics.Trace.WriteLine(diagnostic.ToString());
                throw diagnostic;
            }
            finally
            {
                if (capsule != null)
                {
                    try
                    {
                        if (!capsule.IsValidObject || capsule.Close(false)) OwnedArchives.Remove(capsule);
                        else if (!RetiredArchives.Any(candidate => ReferenceEquals(candidate, capsule))) RetiredArchives.Add(capsule);
                    }
                    catch { if (!RetiredArchives.Any(candidate => ReferenceEquals(candidate, capsule))) RetiredArchives.Add(capsule); }
                    // A failed export must never be published as a valid capsule.
                    // The caller owns cleanup of its unique scratch directory.
                }
                if (!saved && result.Count > 0) result.Clear();
            }
        }

        private static InvalidOperationException DurableExportDiagnostic(Exception failure, string stage, string root,
            string member, string definition, Document source, Document capsule)
        {
            var context = "Export durable refusé [stage=" + stage + "; root=" + (root ?? "")
                + "; member=" + (member ?? "") + "; definition=" + (definition ?? "")
                + "; source=" + DurableDocumentDiagnostic(source) + "; capsule=" + DurableDocumentDiagnostic(capsule) + "]";
            return new InvalidOperationException(context + " " + failure.GetType().FullName + ": " + failure.Message, failure);
        }

        private static Element RequireDurableElement(Document document, string uid)
        {
            var element = document.GetElement(uid);
            if (element == null || !element.IsValidObject)
                throw new InvalidOperationException("Un objet de la capsule a disparu après validation : " + uid);
            return element;
        }

        private static Element RequireDurableElement(Document document, ElementId id)
        {
            var element = document.GetElement(id);
            if (element == null || !element.IsValidObject)
                throw new InvalidOperationException("Un objet de la capsule a disparu pendant le versionnement : " + id.GetIdLongValue());
            return element;
        }

        private static string DurableDocumentDiagnostic(Document document)
        {
            if (ReferenceEquals(document, null)) return "null";
            var identity = RuntimeHelpers.GetHashCode(document).ToString("X");
            try { return identity + (document.IsValidObject ? ":valid:" + document.PathName : ":closed"); }
            catch (Exception ex) { return identity + ":" + ex.GetType().Name; }
        }

        private static string DurableDefinitionDiagnostic(Element definition)
            => definition.UniqueId + "|" + definition.GetType().FullName + "|name=" + definition.Name;

        private static DurableItem ResolveDurableItem(Document source, HistoryRestoreRequest request)
        {
            if (request == null) throw new ArgumentException("Une demande de conversion est absente.");
            var saved = request.Recipe?.Native ?? request.Recipe?.OpeningFallback;
            Element element = string.IsNullOrWhiteSpace(request.SourceUniqueId) ? null : source.GetElement(request.SourceUniqueId);
            if (element == null && !string.IsNullOrEmpty(saved?.MemberSourceUniqueId)) element = source.GetElement(saved.MemberSourceUniqueId);
            if (element == null && saved?.SourceElementId > 0) element = source.GetElement(ElementIdExtensions.CreateElementId(saved.SourceElementId));
            if (element == null || (!ElementHistoryOpeningArchive.IsEligibleElement(element) && !(element is MEPSystem)) || IsCalo(element))
                throw new InvalidOperationException("L’objet demandé est absent du checkpoint ou exclu de la restauration.");
            if (!string.IsNullOrEmpty(saved?.MemberSourceUniqueId) && saved.MemberSourceUniqueId != element.UniqueId)
                throw new InvalidOperationException("L’identité du membre ne correspond pas au checkpoint.");
            var item = new DurableItem { Request = request, Element = element, Root = Root(element) };
            if (saved?.ExcludedElementIds?.Count > 0)
            {
                var excluded = new HashSet<int>(saved.ExcludedElementIds);
                if (Aggregate(source, item.Root).Any(member => excluded.Contains(member.Id.GetIdValue())))
                    throw new InvalidOperationException("Un membre de l’ensemble a changé après la copie initiale.");
            }
            if (element is MEPSystem)
            {
                item.DirectSystem = ElementHistoryReconstruction.CaptureDirectForRestore(element);
                if (item.DirectSystem == null) throw new InvalidOperationException("La recette directe du système MEP n’est pas exploitable ; aucune copie native du système n’est autorisée.");
                if (!string.IsNullOrEmpty(item.DirectSystem.Type))
                {
                    item.SystemDefinition = source.GetElement(item.DirectSystem.Type) as ElementType;
                    if (item.SystemDefinition == null) throw new InvalidOperationException("La définition du système MEP est absente.");
                }
            }
            return item;
        }

        private static HistoryNativeArtifact DurableArtifact(DurableItem item, string path, string copiedRoot, string version)
        {
            var saved = item.Request.Recipe?.Native ?? item.Request.Recipe?.OpeningFallback;
            var artifact = new HistoryNativeArtifact
            {
                File = path, Ready = true, TypeVersion = version, RootUniqueId = copiedRoot,
                RootSourceUniqueId = item.Root.UniqueId, MemberSourceUniqueId = item.Element.UniqueId,
                RequestedUtc = saved?.RequestedUtc, StartedUtc = DateTime.UtcNow, ReadyUtc = DateTime.UtcNow
            };
            if (HasMepSystemType(item.Element))
            {
                var systemType = ReadMepSystemType(item.Element);
                artifact.SourceSystemTypeDefined = saved?.SourceSystemTypeDefined ?? (systemType != null);
                artifact.SourceSystemTypeUniqueId = saved?.SourceSystemTypeUniqueId ?? systemType?.UniqueId;
                artifact.SourceSystemTypeName = saved?.SourceSystemTypeName ?? systemType?.Name;
                artifact.SourceConnectedPeerTypeUniqueIds = saved?.SourceConnectedPeerTypeUniqueIds ?? ReadConnectedPeerSystemTypes(item.Element);
            }
            return artifact;
        }

        private static void PreserveDurableLiveMetadata(DurableItem item, HistoryRecipe recipe)
        {
            var live = item.Request.Recipe;
            recipe.Parameters = live?.Parameters ?? recipe.Parameters ?? ElementHistoryReconstruction.CaptureParameters(item.Element);
            if (live?.HistoricalPlacement != null) recipe.HistoricalPlacement = live.HistoricalPlacement;
            if (live?.Connections != null) recipe.Connections = live.Connections;
            if (live?.Ports != null) recipe.Ports = live.Ports;
            if (live?.ElectricalCircuits != null) recipe.ElectricalCircuits = live.ElectricalCircuits;
            if (live?.GeometryRelations?.Count > 0)
                recipe.GeometryRelations = live.GeometryRelations.Concat(recipe.GeometryRelations ?? new List<HistoryGeometryRelation>())
                    .GroupBy(ElementHistoryRelations.Key).Select(group => group.First()).ToList();
            recipe.RestorationOrigins = (live?.RestorationOrigins ?? new List<string>())
                .Concat(recipe.RestorationOrigins ?? new List<string>()).Distinct().ToList();
            recipe.NativeFallbackReason = live?.NativeFallbackReason;
        }

        private static void StampDurableDefinitionPair(Element original, Element copied, string version)
        {
            if (original == null || copied == null) return;
            ElementHistoryRestoration.SetOrigins(copied,
                (ElementHistoryRestoration.GetOrigins(copied) ?? new List<string>())
                .Concat(ElementHistoryRestoration.GetOrigins(original) ?? new List<string>())
                .Where(value => !value.StartsWith(HistoricalDefinitionSourcePrefix, StringComparison.Ordinal))
                .Concat(new[] { original.UniqueId, HistoricalTypeVersionPrefix + version, HistoricalDefinitionSourcePrefix + original.UniqueId }));
            if (original is FamilySymbol sourceSymbol && copied is FamilySymbol copiedSymbol)
                StampDurableDefinitionPair(sourceSymbol.Family, copiedSymbol.Family, version);
        }

        private static void StampDurableDefinition(Element definition, string version)
            => ElementHistoryRestoration.SetOrigins(definition,
                (ElementHistoryRestoration.GetOrigins(definition) ?? new List<string>())
                .Where(value => !value.StartsWith(HistoricalTypeVersionPrefix, StringComparison.Ordinal))
                .Concat(new[] { HistoricalTypeVersionPrefix + version }));

        private static Dictionary<string, Element> StampDurableDependencyOrigins(Document source, Document capsule,
            HashSet<ElementId> templateDefinitions, HashSet<ElementId> renamedTemplateIds, string version,
            IEnumerable<HistoryRecipe> recipes, Action<string, string> report)
        {
            var sourceDefinitions = DurableDefinitions(source).ToList();
            var byUid = sourceDefinitions.ToDictionary(element => element.UniqueId, StringComparer.Ordinal);
            var byName = sourceDefinitions.GroupBy(DurableDefinitionNameKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var result = new Dictionary<string, Element>(StringComparer.Ordinal);
            var required = new HashSet<string>(recipes.SelectMany(DurableDefinitionReferences)
                .Where(uid => uid != null && byUid.ContainsKey(uid)), StringComparer.Ordinal);
            // Definitions can themselves refer to materials, profiles, nested
            // symbols and other types. Follow only real ElementId references.
            var pending = new Queue<Element>(required.Select(uid => byUid[uid]));
            while (pending.Count > 0)
                foreach (Parameter parameter in pending.Dequeue().Parameters)
                    if (parameter.HasValue && parameter.StorageType == StorageType.ElementId)
                    {
                        var reference = source.GetElement(parameter.AsElementId());
                        if (reference != null && byUid.ContainsKey(reference.UniqueId) && required.Add(reference.UniqueId))
                            pending.Enqueue(reference);
                    }
            // A fitting copied without its neighbours does not necessarily bring
            // their system types. Persist these small referenced definitions now.
            var existing = DurableDefinitions(capsule).ToList();
            var knownOrigins = new HashSet<string>(existing.SelectMany(element =>
                ElementHistoryRestoration.GetOrigins(element) ?? new List<string>()), StringComparer.Ordinal);
            var existingNames = new HashSet<string>(existing.Select(DurableDefinitionNameKey), StringComparer.Ordinal);
            var missingDefinitions = required.Select(uid => byUid[uid]).Where(element => !knownOrigins.Contains(element.UniqueId)
                && !existingNames.Contains(DurableDefinitionNameKey(element))).ToList();
            if (missingDefinitions.Any(element => element is Family))
                throw new InvalidOperationException("Une famille de définition requise ne peut pas être copiée indépendamment de son symbole.");
            if (missingDefinitions.Count > 0)
            {
                report("copy_referenced_definitions", string.Join(",", missingDefinitions.Select(element => element.UniqueId)));
                using (var options = Options())
                    ElementTransformUtils.CopyElements(source, missingDefinitions.Select(element => element.Id).ToList(), capsule, Transform.Identity, options);
                capsule.Regenerate();
            }
            foreach (var id in DurableDefinitions(capsule).Select(element => element.Id).ToList())
            {
                var copied = RequireDurableElement(capsule, id);
                report("stamp_dependency_origin", copied.UniqueId);
                var origins = ElementHistoryRestoration.GetOrigins(copied) ?? new List<string>();
                var proven = origins.Where(value => value.StartsWith(HistoricalDefinitionSourcePrefix, StringComparison.Ordinal))
                    .Select(value => value.Substring(HistoricalDefinitionSourcePrefix.Length)).Where(byUid.ContainsKey).Distinct().ToList();
                var candidates = (proven.Count > 0 ? proven : origins.Where(byUid.ContainsKey).Distinct().ToList())
                    .Select(uid => byUid[uid]).ToList();
                if (candidates.Count == 0 && byName.TryGetValue(DurableDefinitionNameKey(copied), out var named)) candidates = named;
                if (copied is ElementType && templateDefinitions.Contains(copied.Id) && !renamedTemplateIds.Contains(copied.Id))
                {
                    if (candidates.Any(candidate => required.Contains(candidate.UniqueId)) || proven.Count > 0)
                        throw new InvalidOperationException("Une définition stock non versionnable serait substituée à une définition requise : "
                            + DurableDefinitionDiagnostic(copied));
                    continue;
                }
                if (candidates.Count == 1)
                {
                    StampDurableDefinitionPair(candidates[0], copied, version);
                    result.Add(copied.UniqueId, candidates[0]);
                }
                else if (!templateDefinitions.Contains(copied.Id) && candidates.Any(candidate => required.Contains(candidate.UniqueId)))
                    throw new InvalidOperationException("L’identité d’une définition requise est ambiguë : " + copied.Name);
            }
            var preserved = new HashSet<string>(result.Values.Select(element => element.UniqueId), StringComparer.Ordinal);
            var missing = required.Where(uid => !preserved.Contains(uid)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("Des définitions requises ne sont pas incluses dans la capsule : " + string.Join(", ", missing));
            return result;
        }

        private static IEnumerable<Element> DurableDefinitions(Document document)
            => new FilteredElementCollector(document).WhereElementIsElementType().ToElements()
                .Concat(new FilteredElementCollector(document).OfClass(typeof(Level)).ToElements())
                .Concat(new FilteredElementCollector(document).OfClass(typeof(Material)).ToElements())
                .Concat(new FilteredElementCollector(document).OfClass(typeof(Family)).ToElements())
                .Where(element => !IsPresentationDefinition(element));

        private static string DurableDefinitionNameKey(Element element)
            => element.GetType().FullName + "|" + (element.Category?.Id.GetIdLongValue().ToString() ?? "")
                + "|" + (element is ElementType type ? type.FamilyName : "") + "|" + element.Name;

        private static IEnumerable<string> DurableDefinitionReferences(HistoryRecipe recipe)
        {
            if (recipe == null) yield break;
            yield return recipe.Type;
            yield return recipe.Level;
            yield return recipe.HistoricalPlacement?.LevelSourceUniqueId;
            yield return recipe.HistoricalPlacement?.HostSourceUniqueId;
            yield return recipe.Network?.SystemType;
            foreach (var parameter in recipe.Parameters ?? new List<HistoryParameter>()) yield return parameter.Reference;
            var native = recipe.Native ?? recipe.OpeningFallback;
            yield return native?.SourceSystemTypeUniqueId;
            foreach (var uid in native?.SourceConnectedPeerTypeUniqueIds ?? new List<string>()) yield return uid;
            foreach (var port in recipe.Ports ?? new List<HistoryPort>()) yield return port.SystemType;
            foreach (var link in recipe.Connections ?? new List<HistoryConnection>())
            { yield return link.Port?.SystemType; yield return link.PeerPort?.SystemType; }
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>())
                foreach (var uid in DurableDefinitionReferences(circuit.Recipe)) yield return uid;
        }

        private static void StampDurablePortVersions(HistoryRecipe recipe, string version)
        {
            foreach (var port in (recipe.Ports ?? new List<HistoryPort>())
                .Concat((recipe.Connections ?? new List<HistoryConnection>()).SelectMany(link => new[] { link.Port, link.PeerPort })))
                if (port != null) port.SystemVersion = string.IsNullOrEmpty(port.SystemType) ? null : version;
            foreach (var circuit in recipe.ElectricalCircuits ?? new List<HistoryCircuit>())
                if (circuit.Recipe != null) StampDurablePortVersions(circuit.Recipe, version);
        }

        private static string VersionedDefinitionName(string name, string version, string sourceIdentity)
            => (string.IsNullOrEmpty(name) ? "Définition" : name.Substring(0, Math.Min(name.Length, 180)))
                + "__BH_" + version.Substring(0, 12) + "_" + DurableDefinitionSuffix(sourceIdentity);

        private static string DurableDefinitionSuffix(string identity)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 12);
        }

        private static void ConfigureDurableFailures(Transaction tx)
            => tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures()).SetClearAfterRollback(true));

        private static void CommitDurableTransaction(Transaction tx)
        {
            if (tx.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit a refusé une transaction de la capsule historique.");
        }
    }
}

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Analyse
{
    internal static partial class ElementHistoryDurableBridge
    {
        private static string ReadyReason(HistoryRecipe recipe) => recipe?.UsesExistingModelDefinitions == true
            ? "État enregistré ; types nécessaires encore présents dans la maquette pour restaurer."
            : "Archive de restauration prête et vérifiée.";

        private static HistoryRecipe CompactDirectRecipe(HistoryRecipe recipe)
        {
            if (recipe == null) throw new InvalidDataException("Aucune recette directe enregistrée pour cet objet.");
            // Clone before removing obsolete backup receipts. The deletion DTO
            // and its last captured instance state must remain immutable.
            var copy = JObject.FromObject(recipe).ToObject<HistoryRecipe>();
            foreach (var node in Recipes(copy))
            {
                if (node == null || node.Kind == "native_archive" || node.Native != null)
                    throw new InvalidDataException("Cet objet ou un circuit dépendant nécessite une copie native ; sa reconstruction directe n’est pas prise en charge.");
                node.OpeningFallback = null;
                node.UsesExistingModelDefinitions = true;
            }
            return copy;
        }

        private static bool HasAutonomousDirectRecipe(HistoryRestoreRequest request)
            => request?.Recipe != null && string.IsNullOrWhiteSpace(request.SuperComponentUniqueId)
                && Recipes(request.Recipe).All(recipe => recipe != null && recipe.Native == null
                    && recipe.Kind != "native_archive" && ElementHistoryReconstruction.ReadRecipe(recipe) != null);

        internal static bool HasCapturedAutonomousRecipe(ElementHistoryEvent ev)
        {
            if (ev?.Action != "delete") return false;
            try { return HasAutonomousDirectRecipe(ToRequest(ev, false)); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("History autonomous capture check: " + ex.Message); return false; }
        }

        // Only the requested element is read, using the already open historical
        // source. CaptureDirectForRestore owns the supported-host/group rules.
        // This DTO is separate from the native request and is never remapped.
        private static HistoryRestoreRequest CaptureAutonomousOpeningRequest(Document source, Prepared item)
        {
            try
            {
                var original = ToRequest(item.Event, false);
                var expected = item.Request.SourceUniqueId;
                if (!string.IsNullOrWhiteSpace(original.SourceUniqueId) && !string.IsNullOrWhiteSpace(expected)
                    && original.SourceUniqueId != expected) return null;
                HistoryRecipe direct;
                Element element = null;
                if (HasAutonomousDirectRecipe(original)) direct = CompactDirectRecipe(original.Recipe);
                else
                {
                    element = source.GetElement(expected);
                    if (element?.IsValidObject != true || ElementHistoryExternalModels.IsExcluded(element)) return null;
                    var artifact = SourceArtifact(item.Request.Recipe);
                    if (artifact?.ExcludedElementIds?.Contains(element.Id.GetIdValue()) == true
                        || artifact?.ExcludedElementIds?.Contains(ElementHistoryNativeArchive.OpeningRoot(element).Id.GetIdValue()) == true) return null;
                    direct = ElementHistoryReconstruction.CaptureDirectForRestore(element);
                    if (direct == null) return null;
                    // Prefer any state captured while the original object was live.
                    if (original.Recipe?.HistoricalPlacement != null) direct.HistoricalPlacement = original.Recipe.HistoricalPlacement;
                    if (original.Recipe?.Parameters != null) direct.Parameters = original.Recipe.Parameters;
                    if (original.Recipe?.Connections != null) direct.Connections = original.Recipe.Connections;
                    if (original.Recipe?.GeometryRelations?.Count > 0)
                        direct.GeometryRelations = original.Recipe.GeometryRelations.Concat(direct.GeometryRelations ?? new List<HistoryGeometryRelation>())
                            .GroupBy(ElementHistoryRelations.Key).Select(group => group.First()).ToList();
                    direct = CompactDirectRecipe(direct);
                }
                var result = new HistoryRestoreRequest {
                    SourceUniqueId = string.IsNullOrWhiteSpace(original.SourceUniqueId) ? expected : original.SourceUniqueId,
                    Label = original.Label ?? item.Request.Label, Category = original.Category ?? item.Request.Category,
                    Recipe = direct, CaptureFailure = null, DeletionAudit = original.DeletionAudit,
                    SuperComponentUniqueId = original.SuperComponentUniqueId,
                    FamilyTypeUniqueId = original.FamilyTypeUniqueId ?? (element as FamilyInstance)?.Symbol?.UniqueId
                };
                return IsReadyDirectRequest(result) ? result : null;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("History autonomous source capture: " + ex.Message); return null; }
        }

        // Migrate one existing capsule member without opening the original model
        // or replacing its native fallback. Only proven historical UIDs survive.
        internal static HistoryRestoreRequest CaptureAutonomousNativeRequest(Document source, HistoryRestoreRequest archived)
        {
            if (archived == null || !string.IsNullOrWhiteSpace(archived.SuperComponentUniqueId)) return null;
            try
            {
                if (HasAutonomousDirectRecipe(archived))
                    return AutonomousRequestWithRecipe(archived, CompactDirectRecipe(archived.Recipe), archived.SourceUniqueId);
                var artifact = archived.Recipe?.Native;
                if (source?.IsValidObject != true || artifact == null || !ElementHistoryNativeArchive.Owns(source)) return null;
                var element = artifact.WholeModel
                    ? source.GetElement(ElementIdExtensions.CreateElementId(artifact.SourceElementId))
                    : source.GetElement(artifact.RootUniqueId);
                if (element == null) return null;
                if (!artifact.WholeModel)
                    element = ElementHistoryNativeArchive.ResolveNativeMember(source, element, artifact.MemberSourceUniqueId);
                var historical = artifact.WholeModel ? element.UniqueId : ElementHistoryNativeArchive.NativeHistoricalIdentity(element);
                var expected = archived.SourceUniqueId ?? artifact.MemberSourceUniqueId ?? artifact.RootSourceUniqueId;
                if (string.IsNullOrWhiteSpace(historical) || string.IsNullOrWhiteSpace(expected) || historical != expected) return null;
                if (artifact.ExcludedElementIds?.Contains(element.Id.GetIdValue()) == true
                    || artifact.ExcludedElementIds?.Contains(ElementHistoryNativeArchive.OpeningRoot(element).Id.GetIdValue()) == true) return null;
                var direct = ElementHistoryReconstruction.CaptureDirectForRestore(element);
                if (direct == null) return null;
                var saved = archived.Recipe;
                if (saved.HistoricalPlacement != null) direct.HistoricalPlacement = saved.HistoricalPlacement;
                if (saved.Parameters != null) direct.Parameters = saved.Parameters;
                if (saved.Connections != null) direct.Connections = saved.Connections;
                if (saved.Ports?.Count > 0) direct.Ports = saved.Ports;
                if (saved.GeometryRelations != null) direct.GeometryRelations = saved.GeometryRelations;
                if (saved.RestorationOrigins != null) direct.RestorationOrigins = saved.RestorationOrigins;
                if (!string.IsNullOrWhiteSpace(direct.FaceReference))
                {
                    // A stable geometry reference contains local Revit face IDs;
                    // string substitution cannot prove a reference across copies.
                    if (string.IsNullOrWhiteSpace(saved.FaceReference)) return null;
                    direct.FaceReference = saved.FaceReference;
                }
                direct = CompactDirectRecipe(direct);
                if (!artifact.WholeModel)
                {
                    var originalJson = JObject.FromObject(saved);
                    originalJson.Remove("Native"); originalJson.Remove("OpeningFallback");
                    var knownHistorical = new HashSet<string>(originalJson.Descendants().OfType<JValue>()
                        .Where(value => value.Type == JTokenType.String).Select(value => (string)value)
                        .Where(IsAutonomousUniqueId), StringComparer.Ordinal) { expected };
                    var remapped = new Dictionary<string, string>(StringComparer.Ordinal);
                    string OriginalUid(string uid)
                    {
                        if (remapped.TryGetValue(uid, out var cached)) return cached;
                        var referenced = source.GetElement(uid);
                        if (referenced == null)
                        {
                            if (knownHistorical.Contains(uid)) return remapped[uid] = uid;
                            throw new InvalidDataException("Référence JSON sans provenance historique : " + uid);
                        }
                        var candidates = new[] { ElementHistoryNativeArchive.NativeHistoricalIdentity(referenced),
                            ElementHistoryNativeArchive.ReadHistoricalDefinitionSource(referenced) }
                            .Where(IsAutonomousUniqueId).Distinct(StringComparer.Ordinal).ToList();
                        if (candidates.Count != 1) throw new InvalidDataException("Référence de capsule absente ou ambiguë : " + uid);
                        return remapped[uid] = candidates[0];
                    }
                    var json = JObject.FromObject(direct);
                    foreach (var value in json.Descendants().OfType<JValue>().Where(value => value.Type == JTokenType.String).ToList())
                    {
                        var uid = (string)value;
                        if (!IsAutonomousUniqueId(uid)) continue;
                        var property = value.Parent as JProperty;
                        if (property?.Name == "Text" || property?.Name == "Name" || property?.Name == "Label") continue;
                        value.Value = OriginalUid(uid);
                    }
                    direct = json.ToObject<HistoryRecipe>();
                }
                var result = AutonomousRequestWithRecipe(archived, direct, expected);
                return IsReadyDirectRequest(result) ? result : null;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("History capsule JSON migration: " + ex.Message); return null; }
        }

        private static bool IsAutonomousUniqueId(string value)
            => !string.IsNullOrWhiteSpace(value) && value.Length > 37 && value[36] == '-'
                && Guid.TryParse(value.Substring(0, 36), out _) && value.Substring(37).All(Uri.IsHexDigit);

        private static HistoryRestoreRequest AutonomousRequestWithRecipe(HistoryRestoreRequest original, HistoryRecipe recipe, string sourceUid)
            => new HistoryRestoreRequest { SourceUniqueId = sourceUid, Label = original.Label, Category = original.Category,
                Recipe = recipe, CaptureFailure = null, DeletionAudit = original.DeletionAudit,
                SuperComponentUniqueId = original.SuperComponentUniqueId, FamilyTypeUniqueId = original.FamilyTypeUniqueId };

        private static void MakeUnavailable(HistoryRestoreRequest request, string reason)
        {
            request.Recipe = null;
            request.CaptureFailure = "État non récupérable : " + reason;
        }

        private static void PublishPreparedAsync(List<Prepared> prepared)
        {
            Io = Task.Run(() =>
            {
                foreach (var item in prepared)
                    try
                    {
                        if (IsReadyDirectRequest(item.Request)) PublishAutonomousPrepared(item);
                        else Publish(item);
                    }
                    catch (Exception ex) { RecordFailure(EventId(item.Event), ex); }
            });
        }

        private static void ProcessDirectDefinitions(Application application, List<ElementHistoryEvent> jobs, ElementHistoryEvent first)
        {
            var prepared = new List<Prepared>();
            var capturedOnly = HasCapturedAutonomousRecipe(first);
            foreach (var ev in jobs)
            {
                if (prepared.Count >= 8) break;
                lock (Sync) if (Errors.ContainsKey(EventId(ev))) continue;
                if (capturedOnly && !HasCapturedAutonomousRecipe(ev)) continue;
                var request = ToRequest(ev, false);
                if (request.Recipe?.Native != null || request.Recipe?.Kind == "native_archive") continue;
                try { request.Recipe = CompactDirectRecipe(request.Recipe); request.CaptureFailure = null; }
                catch (InvalidDataException ex) { MakeUnavailable(request, ex.Message); }
                prepared.Add(new Prepared { Event = ev, Request = request });
            }
            if (prepared.Count == 0) throw new InvalidDataException("Aucune recette directe disponible pour cette préparation.");
            // No Revit source, definition closure or native export is needed.
            // Purging required model types prevents restoration by contract.
            PublishPreparedAsync(prepared);
        }

        private static void PrepareCompactOpeningRequest(Document source, HistoryRestoreRequest request)
        {
            var saved = request.Recipe;
            var artifact = SourceArtifact(saved);
            if (artifact?.WholeModel != true || saved?.Native?.WholeModel != true)
                throw new InvalidDataException("Une source native imbriquée ne permet pas une reconstruction directe complète.");
            var element = source.GetElement(ElementIdExtensions.CreateElementId(artifact.SourceElementId));
            if (element == null) throw new InvalidDataException("Objet absent de la copie d’ouverture.");
            if ((!string.IsNullOrWhiteSpace(request.SourceUniqueId) && request.SourceUniqueId != element.UniqueId)
                || (!string.IsNullOrWhiteSpace(artifact.MemberSourceUniqueId) && artifact.MemberSourceUniqueId != element.UniqueId))
                throw new InvalidDataException("L’identité de l’objet ne correspond pas à la copie d’ouverture.");
            request.OpeningClassification = new HistoryOpeningClassification {
                SourceElementId = element.Id.GetIdValue(), SourceUniqueId = element.UniqueId,
                IsDocumentElement = ElementHistoryNativeArchive.IsDocumentModelElement(source,element),
                Class = element.GetType().FullName, Category = element.Category?.Name,
                CategoryId = element.Category?.Id.GetIdLongValue() ?? 0, ViewSpecific = element.ViewSpecific,
                HasBoundingBox = element.GetType() == typeof(Element) && element.Category == null && element.get_BoundingBox(null) != null
            };
            // Old immutable failure records are reclassified without rewriting
            // their audit or attempting to publish a different conversion.
            if (request.ClassificationOnly) return;
            request.Category = element.Category?.Name ?? request.Category;
            request.SourceUniqueId = element.UniqueId;
            if (request.OpeningClassification.IsExcluded())
            {
                request.Recipe = null;
                request.CaptureFailure = "Hors périmètre 3D : isolant, annotation, axe ou dépendance interne identifié dans la copie d’ouverture.";
                return;
            }
            if (ElementHistoryExternalModels.IsExcluded(element))
                throw new InvalidDataException("Lien/import externe exclu de la récupération d’objets.");
            if (artifact.ExcludedElementIds?.Contains(element.Id.GetIdValue()) == true
                || artifact.SourceRootElementId > 0 && artifact.ExcludedElementIds?.Contains(artifact.SourceRootElementId) == true)
                throw new InvalidDataException("L’objet a changé après l’ouverture ; la copie ne prouve pas son dernier état.");
            string diagnostic = null;
            var direct = ElementHistoryReconstruction.CaptureDirectForRestore(element, text => diagnostic = text);
            if (direct == null)
                throw new InvalidDataException(diagnostic ?? "Catégorie non reconstructible directement avec les types encore présents dans la maquette.");
            // Prefer the lightweight state captured while the object was live.
            if (saved.HistoricalPlacement != null) direct.HistoricalPlacement = saved.HistoricalPlacement;
            if (saved.Parameters != null) direct.Parameters = saved.Parameters;
            if (saved.GeometryRelations?.Count > 0)
                direct.GeometryRelations = saved.GeometryRelations.Concat(direct.GeometryRelations ?? new List<HistoryGeometryRelation>())
                    .GroupBy(ElementHistoryRelations.Key).Select(group => group.First()).ToList();
            if (saved.Connections != null) direct.Connections = saved.Connections;
            request.Recipe = CompactDirectRecipe(direct);
            request.CaptureFailure = null;
            request.SourceUniqueId = element.UniqueId;
            request.Category = element.Category?.Name ?? request.Category;
            request.SuperComponentUniqueId = (element as FamilyInstance)?.SuperComponent?.UniqueId;
            request.FamilyTypeUniqueId = (element as FamilyInstance)?.Symbol?.UniqueId;
        }
    }
}

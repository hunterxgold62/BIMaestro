using Autodesk.Revit.DB;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        static bool IsRapidGeneratedNativeClass(Type runtimeType, long? categoryId) => runtimeType == typeof(Element)
            && categoryId.HasValue && ProtectedGeneratedCategories.Contains(categoryId.Value);

        static bool IsRapidProtectedBaseline(CatalogEntry entry)
        {
            if (entry == null || entry.ElementType || string.IsNullOrWhiteSpace(entry.Uid)) return false;
            return entry.Class switch
            {
                "LinearDimension" => entry.CategoryId == -2000261 && entry.ProtectionReason == "not-model-category",
                "SpotDimension" => entry.CategoryId == -2000263 && entry.ProtectionReason == "view-or-view-specific",
                "IndependentTag" => entry.CategoryId == -2008047 && entry.ProtectionReason == "view-or-view-specific",
                "DataStorage" => entry.CategoryId == null && entry.ProtectionReason == "definition-or-system",
                "Element" => entry.CategoryId.HasValue && ProtectedGeneratedCategories.Contains(entry.CategoryId.Value)
                    && entry.ProtectionReason == "generated-model-category",
                _ => false
            };
        }

        static string RapidExcludedRequestKind(CatalogEntry baseline, bool baselineIsPhysical, long eventId, long requestAuditId,
            string requestUid, bool recipeNull, string captureFailure, bool hasLiveExclusion,
            string liveUid, string liveClass, long? liveCategory)
        {
            if (baselineIsPhysical || !IsRapidProtectedBaseline(baseline) || baseline.Id != eventId || requestAuditId != eventId || !recipeNull) return null;
            if (captureFailure == "Hors périmètre de restauration : objet exclu identifié avant sa suppression (annotation, axe, isolant ou lien/import externe)."
                && hasLiveExclusion && requestUid == baseline.Uid && liveUid == baseline.Uid
                && liveClass == "Autodesk.Revit.DB." + baseline.Class && liveCategory == baseline.CategoryId)
                return "LiveExcludedMetadata";
            if (captureFailure == "État non récupérable : Catégorie non reconstructible directement avec les types encore présents dans la maquette."
                && !hasLiveExclusion && string.IsNullOrEmpty(requestUid)) return "ProtectedBaselineExplicitUnsupported";
            return null;
        }

        IList SelectRapidRestorationRequests(IList allRequests, List<object> events)
        {
            if (!config.RapidModifiedDelete || !IsDurableRestore) return allRequests;
            var selected = (IList)Activator.CreateInstance(allRequests.GetType());
            var records = new List<object>();
            if (allRequests.Count != events.Count || events.Count != changedIds.Length
                || !events.Select(ev => Convert.ToInt64(Read(ev, "ElementId"))).OrderBy(id => id).SequenceEqual(changedIds.OrderBy(id => id)))
                throw new InvalidOperationException("Rapid request filtering requires all actual deletion events and one real UI request per event.");
            for (int index = 0; index < events.Count; index++)
            {
                var ev = events[index]; var request = allRequests[index]; long id = Convert.ToInt64(Read(ev, "ElementId"));
                if (!catalog.TryGetValue(id, out var baseline)) throw new InvalidOperationException("Deleted event has no independent pre-edit catalog entry: " + id);
                bool physical = deletedPhysicalIds.Contains(id);
                bool recipeNull = Read(request, "Recipe") == null;
                string requestUid = Read(request, "SourceUniqueId")?.ToString(), failure = Read(request, "CaptureFailure")?.ToString();
                long requestAuditId = Convert.ToInt64(Read(Read(request, "DeletionAudit"), "ElementId") ?? -1L);
                string kind = null; bool live = false; string liveUid = null, liveClass = null; long? liveCategory = null;
                if (physical)
                {
                    if (baseline.ProtectionReason != null || requestUid != baseline.Uid || recipeNull || requestAuditId != id)
                        throw new InvalidOperationException("A physical restoration request is missing identity/recipe or was misclassified: " + id);
                    selected.Add(request); kind = "PhysicalRestoration";
                }
                else
                {
                    live = (bool)Call(durableBridge, "HasLiveExclusion", ev);
                    var delta = (IDictionary)Read(ev, "Delta");
                    if (delta?.Contains("excluded3D") == true && delta["excluded3D"] is object metadata)
                    {
                        var json = AppDomain.CurrentDomain.GetAssemblies().First(assembly => assembly.GetName().Name == "Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true);
                        using var parsed = JsonDocument.Parse((string)Call(json, "SerializeObject", metadata)); var value = parsed.RootElement;
                        if (value.ValueKind == JsonValueKind.Object)
                        {
                            liveUid = value.TryGetProperty("SourceUniqueId", out var uid) ? uid.GetString() : null;
                            liveClass = value.TryGetProperty("Class", out var cls) ? cls.GetString() : null;
                            liveCategory = value.TryGetProperty("CategoryId", out var category) && category.TryGetInt64(out var categoryId) ? categoryId : null;
                        }
                    }
                    kind = RapidExcludedRequestKind(baseline, physicalCandidateIds.Contains(id), id, requestAuditId,
                        requestUid, recipeNull, failure, live, liveUid, liveClass, liveCategory);
                    if (kind == null) throw new InvalidOperationException("Nonphysical event has no exact independent protected baseline and explicit production acknowledgement/refusal: " + id);
                }
                records.Add(new { SourceId = id, Baseline = baseline, EventElementId = id, RequestAuditElementId = requestAuditId,
                    RequestSourceUid = requestUid, RequestRecipeNull = recipeNull, CaptureFailure = failure, Physical = physical,
                    EvidenceKind = kind, LiveExclusionValidated = live, LiveSourceUid = liveUid, LiveClass = liveClass, LiveCategoryId = liveCategory });
            }
            bool coverage = selected.Count == deletedPhysicalIds.Length && selected.Cast<object>().Select(request => (string)Read(request, "SourceUniqueId"))
                .OrderBy(uid => uid).SequenceEqual(deletedPhysicalIds.Select(id => specs[id].Uid).OrderBy(uid => uid));
            Write("rapid-request-selection.json", new { AtUtc = DateTime.UtcNow, Passed = coverage, FullRequestsCount = allRequests.Count,
                RestorationRequestsCount = selected.Count, ExcludedNonPhysicalCount = allRequests.Count - selected.Count,
                ActualDeletedEventIds = changedIds, ActualDeletedPhysicalIds = deletedPhysicalIds, Records = records,
                Note = "All natural events and UI conversions remain recorded. Restore receives every and only independently admissible deleted physical object. Live metadata exclusions and protected-baseline explicit unsupported records remain separate; missing recipes or identity failures alone never prove exclusion." });
            if (!coverage) throw new InvalidOperationException("Physical restoration selection does not exactly cover the actual deleted physical denominator.");
            return selected;
        }

        bool IsRapidGeneratedRecord(Element element) => config.RapidModifiedDelete
            && IsRapidGeneratedNativeClass(element.GetType(), element.Category?.Id.Value)
            && catalog.Values.Any(baseline => changedIds.Contains(baseline.Id) && baseline.Class == "Element"
                && baseline.CategoryId == element.Category?.Id.Value && baseline.ProtectionReason == "generated-model-category");
    }
}

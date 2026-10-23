using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        static readonly long[] BalusterDiagnosticIds = { 557051, 557052, 557053, 557054, 557055, 557056 };

        object ElementIdParameters(Element element)
        {
            if (element == null) return null;
            return element.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.ElementId)
                .Select(p =>
                {
                    var id = Safe(() => p.AsElementId());
                    var target = id == null || id == ElementId.InvalidElementId ? null : Safe(() => element.Document.GetElement(id));
                    return new
                    {
                        ParameterId = p.Id.Value,
                        Name = Safe(() => p.Definition.Name),
                        p.IsReadOnly,
                        p.HasValue,
                        Value = id?.Value,
                        DisplayValue = Safe(() => p.AsValueString()),
                        Target = target == null ? null : new { Id = target.Id.Value, Uid = target.UniqueId, Class = target.GetType().Name, Category = target.Category?.Name, Name = Safe(() => target.Name) }
                    };
                }).OrderBy(p => p.ParameterId).ToArray();
        }

        object DiagnosticElement(Element element)
        {
            if (element == null) return null;
            var type = element.Document.GetElement(element.GetTypeId());
            return new
            {
                Spec = Capture(element),
                Origins = Origins(element),
                OwnerViewId = element.OwnerViewId.Value,
                GroupId = element.GroupId.Value,
                LevelId = element.LevelId.Value,
                WorksetId = element.WorksetId.IntegerValue,
                IsTransient = Safe(() => element.IsTransient),
                Parameters = ElementIdParameters(element),
                Type = type == null ? null : new { Id = type.Id.Value, Uid = type.UniqueId, Class = type.GetType().Name, Name = Safe(() => type.Name), Parameters = ElementIdParameters(type) }
            };
        }

        object BalusterPatternInfo(Document document, object info)
        {
            if (info == null) return null;
            var familyId = Safe(() => Read(info, "BalusterFamilyId") as ElementId);
            var target = familyId == null ? null : document.GetElement(familyId);
            return new
            {
                Name = Safe(() => Read(info, "Name")), FamilyId = familyId?.Value,
                Target = target == null ? null : new { Id = target.Id.Value, Uid = target.UniqueId, Class = target.GetType().Name, Name = Safe(() => target.Name), TypeId = target.GetTypeId().Value },
                BaseReferenceName = Safe(() => Read(info, "BaseReferenceName")), TopReferenceName = Safe(() => Read(info, "TopReferenceName")),
                BaseOffset = Safe(() => Read(info, "BaseOffset")), TopOffset = Safe(() => Read(info, "TopOffset")),
                Offset = Safe(() => Read(info, "Offset")), DistanceFromPreviousOrSpace = Safe(() => Read(info, "DistanceFromPreviousOrSpace"))
            };
        }

        object RailingPattern(Element type)
        {
            try
            {
                var placement = Read(type, "BalusterPlacement"); var pattern = Read(placement, "BalusterPattern");
                var posts = Read(placement, "PostPattern"); var infos = new List<object>();
                int count = pattern == null ? 0 : (int)pattern.GetType().GetMethod("GetBalusterCount").Invoke(pattern, null);
                for (int index = 0; index < count; index++) infos.Add(BalusterPatternInfo(type.Document, pattern.GetType().GetMethod("GetBaluster").Invoke(pattern, new object[] { index })));
                var perTreadId = Read(placement, "BalusterPerTreadFamilyId") as ElementId;
                var excessId = Read(pattern, "ExcessLengthFillBalusterId") as ElementId;
                return new
                {
                    Id = type.Id.Value, Uid = type.UniqueId, Name = Safe(() => type.Name), Balusters = infos,
                    StartPost = BalusterPatternInfo(type.Document, Read(posts, "StartPost")),
                    EndPost = BalusterPatternInfo(type.Document, Read(posts, "EndPost")),
                    CornerPost = BalusterPatternInfo(type.Document, Read(posts, "CornerPost")),
                    PerTreadFamilyId = perTreadId?.Value, PerTreadFamilyUid = perTreadId == null ? null : type.Document.GetElement(perTreadId)?.UniqueId,
                    ExcessLengthFillBalusterId = excessId?.Value, ExcessLengthFillBalusterUid = excessId == null ? null : type.Document.GetElement(excessId)?.UniqueId,
                    Error = (string)null
                };
            }
            catch (Exception ex) { return new { Id = type.Id.Value, Uid = type.UniqueId, Error = ex.ToString() }; }
        }

        void Diagnose(UIApplication application)
        {
            if (config.Scenario != "diagnostic") throw new InvalidOperationException("The read-only diagnosis requires Scenario=diagnostic.");
            if (doc == null || !doc.IsValidObject || !IsCopy(doc) || !string.Equals(doc.PathName, modelPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Diagnosis is restricted to the fresh run copy.");
            if (!string.IsNullOrWhiteSpace(config.ReferenceInventoryPath)) { DiagnoseRestoredOrigins(application); return; }
            var watch = Stopwatch.StartNew();
            bool modifiedBefore = doc.IsModified;
            phase = "diagnosing-readonly";
            RefreshExcavations(doc);
            var targets = BalusterDiagnosticIds.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null).ToArray();
            var targetIds = new HashSet<long>(BalusterDiagnosticIds);
            var targetTypeIds = new HashSet<long>(targets.Select(e => e.GetTypeId().Value));
            var owners = new List<object>();
            var queryErrors = new List<object>();
            var all = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
                .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElements()).ToArray();
            var candidates = all.Where(e => e is FamilySymbol || e.GetType().Name is "Railing" or "Stairs" or "RailingType" or "StairsType" or "TopRail" or "HandRail").ToArray();
            foreach (var owner in candidates)
            {
                long[] dependentIds = Array.Empty<long>();
                string error = null;
                try { dependentIds = owner.GetDependentElements(null).Select(id => id.Value).ToArray(); }
                catch (Exception ex) { error = ex.GetType().FullName + ": " + ex.Message; queryErrors.Add(new { Id = owner.Id.Value, Class = owner.GetType().Name, Error = error }); }
                var direct = dependentIds.Where(targetIds.Contains).ToArray();
                var symbols = dependentIds.Where(targetTypeIds.Contains).ToArray();
                var references = owner.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.ElementId)
                    .Select(p => new { ParameterId = p.Id.Value, Name = Safe(() => p.Definition.Name), TargetId = Safe(() => p.AsElementId()?.Value, (long?)null) })
                    .Where(p => p.TargetId.HasValue && (targetIds.Contains(p.TargetId.Value) || targetTypeIds.Contains(p.TargetId.Value))).ToArray();
                if (direct.Length > 0 || symbols.Length > 0 || references.Length > 0)
                    owners.Add(new { Owner = DiagnosticElement(owner), MatchingBalusterIds = direct, MatchingTypeIds = symbols, ReferencingParameters = references, AllDependentIds = dependentIds, Error = error });
            }
            var railingInventory = candidates.Where(e => e.GetType().Name is "Railing" or "RailingType" or "Stairs" or "StairsType")
                .Select(DiagnosticElement).ToArray();
            var topography = new List<object>();
            foreach (var terrain in all.OfType<Toposolid>())
            {
                try
                {
                    var intersections = terrain.GetIntersectingElementData().Select(data => new
                    {
                        Type = data.IntersectionType.ToString(),
                        IntersectedId = data.IntersectedElementId.Value,
                        IntersectedUid = doc.GetElement(data.IntersectedElementId)?.UniqueId,
                        IntersectingId = data.IntersectingElementId.Value,
                        IntersectingUid = doc.GetElement(data.IntersectingElementId)?.UniqueId,
                        data.IntersectionVolume
                    }).ToArray();
                    topography.Add(new { Id = terrain.Id.Value, Uid = terrain.UniqueId, Spec = Capture(terrain), Intersections = intersections, Error = (string)null });
                }
                catch (Exception ex) { topography.Add(new { Id = terrain.Id.Value, Uid = terrain.UniqueId, Error = ex.ToString() }); }
            }
            var report = new
            {
                AtUtc = DateTime.UtcNow, Scenario = config.Scenario, ReadOnly = true, Document = doc.PathName,
                Product = product.Location, ProductSha256 = Sha(product.Location), ModifiedBefore = modifiedBefore, ModifiedAfter = doc.IsModified,
                RequestedIds = BalusterDiagnosticIds, MissingIds = BalusterDiagnosticIds.Except(targets.Select(e => e.Id.Value)).ToArray(),
                Targets = targets.Select(DiagnosticElement).ToArray(), Owners = owners, RailingAndStairs = railingInventory,
                RailingTypePatterns = candidates.Where(e => e.GetType().Name == "RailingType").Select(RailingPattern).ToArray(), Topography = topography,
                CandidatesQueried = candidates.Length, QueryErrors = queryErrors, Milliseconds = watch.Elapsed.TotalMilliseconds,
                Note = "No transaction, deletion, save, recipe capture or restoration was invoked by this diagnostic. Production background hooks may still run between callbacks. Dependent-elements and ElementId-parameter relations are exported separately; absence is not proof that no undocumented owner exists."
            };
            Write("baluster-diagnostic.json", report); Record("diagnosis_complete", new { report.Milliseconds, Targets = targets.Length, Owners = owners.Count, Errors = queryErrors.Count });
            phase = "diagnosis-complete";
        }

    }
}

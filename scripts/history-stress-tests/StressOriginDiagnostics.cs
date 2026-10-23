using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        public sealed class ReferenceInventory
        {
            public List<Spec> Specs { get; set; } = new();
        }

        void DiagnoseRestoredOrigins(UIApplication application)
        {
            var watch = Stopwatch.StartNew(); bool modifiedBefore = doc.IsModified; phase = "diagnosing-restored-origins";
            var reference = JsonSerializer.Deserialize<ReferenceInventory>(File.ReadAllText(config.ReferenceInventoryPath));
            if (reference?.Specs == null) throw new InvalidOperationException("Reference inventory has no model specifications.");
            using var ownershipJson = JsonDocument.Parse(File.ReadAllText(config.ReferenceOwnershipPath));
            var expectedById = reference.Specs.ToDictionary(spec => spec.Id);
            var selectedBars = BalusterDiagnosticIds.Select(id => expectedById.TryGetValue(id, out var expected) ? expected : null).ToArray();
            if (selectedBars.Any(spec => spec == null || string.IsNullOrWhiteSpace(spec.Uid))) throw new InvalidOperationException("Reference inventory does not contain all six original baluster UIDs.");
            var live = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().ToArray();
            var origins = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            foreach (var element in live)
                foreach (var origin in Origins(element).Append(element.UniqueId).Distinct())
                {
                    if (!origins.TryGetValue(origin, out var matches)) origins[origin] = matches = new List<Element>();
                    matches.Add(element);
                }
            Element Unique(string uid) => uid != null && origins.TryGetValue(uid, out var matches) && matches.Select(e => e.UniqueId).Distinct().Count() == 1 ? matches[0] : null;
            bool Maps(string expected, string actual) => Unique(expected)?.UniqueId == actual;
            RefreshExcavations(doc);
            var railings = live.OfType<Railing>().ToArray(); var railingMembers = new Dictionary<long, HashSet<long>>(); var errors = new List<string>();
            foreach (var railing in railings)
                try { railingMembers[railing.Id.Value] = railing.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance))).Select(id => id.Value).ToHashSet(); }
                catch (Exception ex) { errors.Add("railing-dependents:" + railing.Id.Value + ":" + ex); }
            var barRecords = new List<object>(); var actualBarUids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var expected in selectedBars)
            {
                var actualElement = Unique(expected.Uid); var actual = actualElement == null ? null : Capture(actualElement);
                var referenceOwners = ownershipJson.RootElement.GetProperty("Owners").EnumerateArray()
                    .Where(owner => owner.GetProperty("Owner").GetProperty("Spec").GetProperty("Class").GetString() == "Railing"
                        && owner.GetProperty("MatchingBalusterIds").EnumerateArray().Any(id => id.GetInt64() == expected.Id)).ToArray();
                Spec expectedOwner = null; bool ownerReference = false;
                if (referenceOwners.Length == 1)
                {
                    var originalOwner = referenceOwners[0].GetProperty("Owner").GetProperty("Spec");
                    ownerReference = expectedById.TryGetValue(originalOwner.GetProperty("Id").GetInt64(), out expectedOwner)
                        && expectedOwner.Uid == originalOwner.GetProperty("Uid").GetString();
                }
                var expectedActualOwner = ownerReference ? Unique(expectedOwner.Uid) : null;
                var currentOwners = actualElement == null ? Array.Empty<Railing>() : railings.Where(owner => railingMembers.TryGetValue(owner.Id.Value, out var members) && members.Contains(actualElement.Id.Value)).ToArray();
                bool owner = ownerReference && expectedActualOwner != null && currentOwners.Length == 1 && currentOwners[0].UniqueId == expectedActualOwner.UniqueId;
                var ownerActual = currentOwners.Length == 1 ? Capture(currentOwners[0]) : null;
                bool ownerBBox = expectedOwner != null && ownerActual != null && SamePoints(expectedOwner.BBox, ownerActual.BBox);
                bool ownerType = expectedOwner != null && ownerActual != null && expectedOwner.Class == ownerActual.Class && expectedOwner.TypeUid == ownerActual.TypeUid;
                bool unique = actualElement != null && actualBarUids.Add(actualElement.UniqueId);
                bool geometry = actual != null && Same(expected.Point, actual.Point) && SamePoints(expected.Curve, actual.Curve) && SamePoints(expected.Transform, actual.Transform);
                bool bbox = actual != null && SamePoints(expected.BBox, actual.BBox);
                bool type = actual != null && expected.Class == actual.Class && expected.TypeUid == actual.TypeUid && expected.TypeName == actual.TypeName && expected.FamilyName == actual.FamilyName;
                bool root = actual != null && Maps(expected.RootUid, actual.RootUid);
                bool clean = actual != null && expected.ProbeErrors.Count == 0 && actual.ProbeErrors.Count == 0;
                barRecords.Add(new { SourceId = expected.Id, SourceUid = expected.Uid, ActualUids = origins.TryGetValue(expected.Uid, out var matches) ? matches.Select(e => e.UniqueId).Distinct().ToArray() : Array.Empty<string>(),
                    IdentityInjective = unique, OwnerReferenceConsistent = ownerReference, OwnerMatches = owner, OwnerBBoxMatches = ownerBBox, OwnerTypeMatches = ownerType, RootMatches = root,
                    TypeMatches = type, GeometryMatches = geometry, BBoxMatches = bbox, ProbesVerified = clean,
                    ExpectedOwner = expectedOwner, ActualOwners = currentOwners.Select(owner => new { Id = owner.Id.Value, Uid = owner.UniqueId, Origins = Origins(owner) }).ToArray(),
                    ActualOwnerSpec = ownerActual, Expected = expected, Actual = actual,
                    Passed = unique && owner && ownerBBox && ownerType && root && type && geometry && bbox && clean });
            }
            var topoRecords = new List<object>();
            foreach (var expected in reference.Specs.Where(spec => spec.Class == "Toposolid"))
            {
                var actualElement = Unique(expected.Uid); var actual = actualElement == null ? null : Capture(actualElement);
                var current = actual?.Excavations ?? new List<ExcavationRelation>();
                bool pairs = actual != null && expected.ExcavationQuerySucceeded && actual.ExcavationQuerySucceeded
                    && expected.Excavations.Count == current.Count && expected.Excavations.All(pair => current.Count(found => found.Kind == pair.Kind
                        && Maps(pair.IntersectedUid, found.IntersectedUid) && Maps(pair.IntersectingUid, found.IntersectingUid)
                        && Math.Abs(pair.VolumeCubicFeet - found.VolumeCubicFeet) <= Math.Max(1e-5, Math.Abs(pair.VolumeCubicFeet) * 1e-8)) == 1);
                bool bbox = actual != null && SamePoints(expected.BBox, actual.BBox);
                bool type = actual != null && expected.Class == actual.Class && expected.TypeUid == actual.TypeUid;
                topoRecords.Add(new { SourceId = expected.Id, SourceUid = expected.Uid, ActualUid = actual?.Uid, ExpectedPairs = expected.Excavations,
                    ActualPairs = current, PairsAndVolumesMatch = pairs, BBoxMatches = bbox, TypeMatches = type, Expected = expected, Actual = actual,
                    Passed = pairs && bbox && type && actual.ProbeErrors.Count == 0 });
            }
            bool unmodified = !modifiedBefore && !doc.IsModified;
            var report = new
            {
                AtUtc = DateTime.UtcNow, Product = product.Location, ProductSha256 = Sha(product.Location), Document = doc.PathName,
                Scope = "Six generated balusters, their exact railing owners, and seven Toposolids with excavation pairs/volumes; not all model objects.",
                ReferenceInventory = config.ReferenceInventoryPath, ReferenceInventorySha256 = Sha(config.ReferenceInventoryPath),
                ReferenceOwnership = config.ReferenceOwnershipPath, ReferenceOwnershipSha256 = Sha(config.ReferenceOwnershipPath),
                ReadOnly = true, ModifiedBefore = modifiedBefore, ModifiedAfter = doc.IsModified, SourceUnmodified = unmodified,
                SixBarSourceUids = selectedBars.Select(spec => spec.Uid).ToArray(), Bars = barRecords, Toposolids = topoRecords,
                Errors = errors, InjectiveSix = actualBarUids.Count == 6,
                AllModelRestorationVerified = false,
                Passed = unmodified && errors.Count == 0 && actualBarUids.Count == 6 && barRecords.All(record => (bool?)Read(record, "Passed") == true)
                    && topoRecords.Count == 7 && topoRecords.All(record => (bool?)Read(record, "Passed") == true),
                Milliseconds = watch.Elapsed.TotalMilliseconds,
                Note = "Read-only inspection of a fresh copy of the saved restored RVT. Source identities come only from the original live inventory; owner references come from the earlier read-only source API diagnostic. No transactions, save, deletion, repair, manual capture or scheduler call are made."
            };
            Write("restored-origin-diagnostic.json", report); Record("restored_origin_diagnosis_complete", new { report.Passed, report.Milliseconds, Bars = barRecords.Count, Toposolids = topoRecords.Count, Errors = errors.Count });
            phase = "origin-diagnosis-complete";
        }
    }
}

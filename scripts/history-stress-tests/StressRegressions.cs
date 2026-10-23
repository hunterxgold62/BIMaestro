using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }
        sealed class RegressionFailures : IFailuresPreprocessor
        {
            readonly Action<string[]> record;
            public RegressionFailures(Action<string[]> record) => this.record = record;
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var messages = accessor.GetFailureMessages();
                record(messages.Select(message => message.GetSeverity() + ": " + message.GetDescriptionText()).ToArray());
                if (messages.Any(message => message.GetSeverity() != FailureSeverity.Warning)) return FailureProcessingResult.ProceedWithRollBack;
                foreach (var message in messages) accessor.DeleteWarning(message);
                return FailureProcessingResult.Continue;
            }
        }
        void StartRegressionTransaction(Transaction transaction)
        {
            transaction.Start();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetClearAfterRollback(true)
                .SetFailuresPreprocessor(new RegressionFailures(messages => Record("regression_api_failures", new { Transaction = transaction.GetName(), Messages = messages }))));
        }
        static void SetMember(object instance, string name, object value)
        {
            var type = instance.GetType();
            if (type.GetField(name, Flags) is FieldInfo field) field.SetValue(instance, value);
            else if (type.GetProperty(name, Flags) is PropertyInfo property) property.SetValue(instance, value);
            else throw new MissingMemberException(type.FullName, name);
        }
        static bool PlacementVerified(Type reconstruction, Element element, object recipe, out string failure)
        {
            var args = new object[] { element, recipe, null, true };
            bool result = (bool)reconstruction.GetMethod("VerifyHistoricalPlacement", Flags).Invoke(null, args);
            failure = args[2] as string; return result;
        }
        bool PortPlacementMatches(Spec expected, Spec actual) => expected.Ports.Count == actual.Ports.Count
            && expected.Ports.All(port => actual.Ports.Count(current => current.Domain == port.Domain && current.Type == port.Type
                && current.Shape == port.Shape && current.Classification == port.Classification && Same(current.Point, port.Point)) == 1);

        object RegressionCoincidentGroup(UIApplication application, bool stamped)
        {
            Document source = null, destination = null; var watch = Stopwatch.StartNew();
            bool sourceClosed = false, destinationClosed = false;
            object result = null;
            try
            {
                source = application.Application.NewProjectDocument(UnitSystem.Metric);
                destination = application.Application.NewProjectDocument(UnitSystem.Metric);
                Group group;
                using (var tx = new Transaction(source, "BIMaestro regression: coincident source"))
                {
                    StartRegressionTransaction(tx); var plane = SketchPlane.Create(source, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    var line = Line.CreateBound(XYZ.Zero, new XYZ(6, 0, 0));
                    var first = source.Create.NewModelCurve(line, plane); var second = source.Create.NewModelCurve(line, plane);
                    group = source.Create.NewGroup(new[] { first.Id, second.Id });
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Coincident source transaction did not commit.");
                }
                if (stamped)
                {
                    using var stamp = new Transaction(source, "BIMaestro regression: stamp existing grouped members"); StartRegressionTransaction(stamp);
                    foreach (var element in group.GetMemberIds().Select(source.GetElement).Append(group))
                        Call(restoration, "SetOrigins", element, new[] { element.UniqueId });
                    if (stamp.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Grouped source provenance transaction did not commit.");
                }
                var originalMembers = group.GetMemberIds().Select(source.GetElement).ToArray();
                var selected = originalMembers.Select(e => e.Id.Value).ToHashSet();
                bool logicalRoot = TryPhysicalGroupRoot(source, group, selected, out var rootScope);
                var sourceOriginsBefore = originalMembers.ToDictionary(e => e.UniqueId, e => Origins(e).OrderBy(uid => uid).ToArray());
                bool coincident = originalMembers.Length == 2 && SamePoints(Capture(originalMembers[0]).BBox, Capture(originalMembers[1]).BBox);
                Element copied;
                IDictionary mapping;
                using (var tx = new Transaction(destination, "BIMaestro regression: copy coincident root"))
                using (var options = new CopyPasteOptions())
                {
                    options.SetDuplicateTypeNamesHandler(new UseDestinationTypes()); StartRegressionTransaction(tx);
                    copied = (Element)Call(native, "CopyRoot", source, group, destination, options);
                    destination.Regenerate();
                    mapping = (IDictionary)Call(native, "MatchAggregate", source, group, destination, copied);
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Coincident destination transaction did not commit.");
                }
                var pairs = new List<object>(); var actualIds = new HashSet<long>(); bool bijective = mapping.Count == originalMembers.Length + 1;
                foreach (var original in originalMembers.Append(group))
                {
                    var actual = mapping[original.UniqueId] as Element;
                    bool unique = actual != null && actualIds.Add(actual.Id.Value);
                    bool ownOrigin = actual != null && Origins(actual).Contains(original.UniqueId);
                    bool otherOrigin = actual != null && originalMembers.Append(group).Any(other => other.UniqueId != original.UniqueId && Origins(actual).Contains(other.UniqueId));
                    bool sameShape = actual != null && actual.GetType() == original.GetType()
                        && SamePoints(Capture(original).BBox, Capture(actual).BBox);
                    bijective &= unique && ownOrigin && !otherOrigin && sameShape;
                    pairs.Add(new { SourceId = original.Id.Value, SourceUid = original.UniqueId, ActualId = actual?.Id.Value, ActualUid = actual?.UniqueId, Unique = unique, OwnOrigin = ownOrigin, OtherSourceOrigin = otherOrigin, SameShape = sameShape });
                }
                bool sourceUntouched = originalMembers.All(e => Origins(e).OrderBy(uid => uid).SequenceEqual(sourceOriginsBefore[e.UniqueId]));
                bool denominatorPreserved = logicalRoot && rootScope.PhysicalLeafIds.OrderBy(id => id).SequenceEqual(selected.OrderBy(id => id));
                result = new { Name = stamped ? "coincident-group-owned-stamped" : "coincident-group-live-unstamped", Passed = coincident && bijective && sourceUntouched && denominatorPreserved, CoincidentPairConfirmed = coincident, Bijective = bijective, SourceOriginsUnchanged = sourceUntouched, LogicalRoot = logicalRoot, DenominatorPreserved = denominatorPreserved, Mapping = pairs, Milliseconds = watch.Elapsed.TotalMilliseconds };
            }
            finally
            {
                if (destination?.IsValidObject == true) destinationClosed = destination.Close(false);
                if (source?.IsValidObject == true) sourceClosed = source.Close(false);
                Record("regression_temporary_documents_closed", new { SourceClosed = sourceClosed, DestinationClosed = destinationClosed });
                if (!sourceClosed || !destinationClosed) throw new InvalidOperationException("A temporary regression project did not close without saving.");
            }
            return result;
        }

        object RegressionPlacement(UIApplication application, long sourceId, bool rotate)
        {
            var source = doc.GetElement(new ElementId(sourceId)) ?? throw new InvalidOperationException("Missing fixture " + sourceId);
            var reconstruction = product.GetType("Analyse.ElementHistoryReconstruction", true);
            var recipe = Activator.CreateInstance(product.GetType("Analyse.HistoryRecipe", true));
            SetMember(recipe, "Parameters", Call(reconstruction, "CaptureParameters", source));
            Call(reconstruction, "CaptureHistoricalPlacement", source, recipe, true);
            var expected = Capture(source); var watch = Stopwatch.StartNew();
            object report; var destination = application.Application.NewProjectDocument(UnitSystem.Metric);
            try
            {
            using (var group = new TransactionGroup(destination, "BIMaestro regression: placement rollback"))
            {
                group.Start();
                try
                {
                    FamilyInstance duplicate;
                    using (var tx = new Transaction(destination, "BIMaestro regression: independent copied fixture"))
                    using (var options = new CopyPasteOptions())
                    {
                        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                        StartRegressionTransaction(tx); var copied = ElementTransformUtils.CopyElements(doc, new[] { source.Id }, destination, Transform.Identity, options).Select(destination.GetElement).OfType<FamilyInstance>().ToArray();
                        if (copied.Length != 1) throw new InvalidOperationException("Copy did not produce exactly one independent family instance.");
                        duplicate = copied[0]; destination.Regenerate();
                        if (Connectors(duplicate).Any(c => Safe(() => c.IsConnected))) throw new InvalidOperationException("Independent regression fixture is already connected; no unsafe movement is permitted.");
                        Call(reconstruction, "RestoreHistoricalPlacement", destination, duplicate, recipe); destination.Regenerate();
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Fixture transaction did not commit.");
                    }
                    bool initial = PlacementVerified(reconstruction, duplicate, recipe, out var initialFailure) && PortPlacementMatches(expected, Capture(duplicate));
                    using (var tx = new Transaction(destination, "BIMaestro regression: deliberate placement fault"))
                    {
                        StartRegressionTransaction(tx);
                        if (rotate) { var origin = duplicate.GetTransform().Origin; ElementTransformUtils.RotateElement(destination, duplicate.Id, Line.CreateBound(origin, origin + XYZ.BasisZ), Math.PI / 2); }
                        else ElementTransformUtils.MoveElement(destination, duplicate.Id, new XYZ(0, 0, 300.0 / 304.8));
                        destination.Regenerate(); if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Deliberate perturbation transaction did not commit.");
                    }
                    bool rejected = !PlacementVerified(reconstruction, duplicate, recipe, out var rejection);
                    var perturbed = Capture(duplicate);
                    using (var tx = new Transaction(destination, "BIMaestro regression: restore historical placement"))
                    {
                        StartRegressionTransaction(tx); Call(reconstruction, "RestoreHistoricalPlacement", destination, duplicate, recipe); destination.Regenerate();
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Historical placement transaction did not commit.");
                    }
                    bool final = PlacementVerified(reconstruction, duplicate, recipe, out var finalFailure); var actual = Capture(duplicate);
                    bool ports = PortPlacementMatches(expected, actual);
                    report = new { Name = rotate ? "rotation-and-ports" : "plant-or-foundation-offset", SourceId = sourceId, Passed = initial && rejected && final && ports, InitialMatches = initial, InitialFailure = initialFailure, PerturbationRejected = rejected, Rejection = rejection, FinalMatches = final, FinalFailure = finalFailure, PortPlacementMatches = ports, Expected = expected, Perturbed = perturbed, Actual = actual, Milliseconds = watch.Elapsed.TotalMilliseconds };
                }
                finally { if (group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack) throw new InvalidOperationException("Regression rollback failed."); }
            }
            return report;
            }
            finally
            {
                bool closed = destination.Close(false);
                Record("regression_placement_document_closed", new { SourceId = sourceId, Closed = closed });
                if (!closed) throw new InvalidOperationException("Temporary placement regression document did not close without saving.");
            }
        }

        object RegressionGeneratedBalusters(UIApplication application)
        {
            var stair = doc.GetElement(new ElementId(556837)) ?? throw new InvalidOperationException("Source stair 556837 is absent.");
            var sourceBars = BalusterDiagnosticIds.Select(id => doc.GetElement(new ElementId(id))).ToArray();
            if (sourceBars.Any(e => e == null)) throw new InvalidOperationException("At least one of the six source balusters is absent.");
            var rootRecords = sourceBars.Select(bar => new { SourceId = bar.Id.Value, RootId = ((Element)Call(native, "OpeningRoot", bar))?.Id.Value }).ToArray();
            bool roots = rootRecords.All(row => row.RootId == stair.Id.Value);
            var destination = application.Application.NewProjectDocument(UnitSystem.Metric);
            object result;
            try
            {
                using var tx = new Transaction(destination, "BIMaestro regression: generated baluster aggregate");
                using var options = new CopyPasteOptions(); options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                StartRegressionTransaction(tx); var copiedStair = (Element)Call(native, "CopyRoot", doc, stair, destination, options); destination.Regenerate();
                var mapping = (IDictionary)Call(native, "MatchAggregate", doc, stair, destination, copiedStair);
                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Generated-baluster copy did not commit.");
                var allDestBars = new FilteredElementCollector(destination).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(e => e.Category?.Id.Value == -2000127).ToArray();
                var sourceRailings = new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>().ToArray();
                var actualRailings = new FilteredElementCollector(destination).OfClass(typeof(Railing)).Cast<Railing>().ToArray();
                long[] Owners(IEnumerable<Railing> railings, Element bar) => railings.Where(r => r.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance))).Contains(bar.Id)).Select(r => r.Id.Value).ToArray();
                var rows = new List<object>(); var actualIds = new HashSet<long>(); bool exact = roots && allDestBars.Length == 6;
                foreach (var bar in sourceBars)
                {
                    var actual = mapping[bar.UniqueId] as Element; var expected = Capture(bar); var current = actual == null ? null : Capture(actual);
                    bool unique = actual != null && actualIds.Add(actual.Id.Value);
                    var sourceOwners = Owners(sourceRailings, bar); var actualOwners = actual == null ? Array.Empty<long>() : Owners(actualRailings, actual);
                    var mappedOwner = sourceOwners.Length == 1 ? mapping[doc.GetElement(new ElementId(sourceOwners[0])).UniqueId] as Element : null;
                    bool owner = sourceOwners.Length == 1 && actualOwners.Length == 1 && mappedOwner?.Id.Value == actualOwners[0];
                    bool bbox = current != null && SamePoints(expected.BBox, current.BBox);
                    bool placement = current != null && Same(expected.Point, current.Point) && SamePoints(expected.Transform, current.Transform);
                    bool type = current != null && expected.Class == current.Class && expected.TypeName == current.TypeName && expected.FamilyName == current.FamilyName;
                    exact &= unique && owner && bbox && placement && type;
                    rows.Add(new { SourceId = bar.Id.Value, SourceUid = bar.UniqueId, ActualId = actual?.Id.Value, ActualUid = actual?.UniqueId,
                        IdentityInjective = unique, SourceRailingIds = sourceOwners, ActualRailingIds = actualOwners, ExpectedActualRailingId = mappedOwner?.Id.Value,
                        OwnerMatches = owner, BBoxMatches = bbox, PlacementMatches = placement, TypeMatches = type, Expected = expected, Actual = current });
                }
                result = new { Name = "stairs-generated-coincident-balusters", Passed = exact, SourceRootChecks = rootRecords,
                    RootMatches = roots, ExpectedBalusters = 6, ActualBalusters = allDestBars.Length, MappingCount = mapping.Count, Records = rows,
                    Note = "Source stays read-only. Mapping is checked before adding any destination origins; exact railing ownership disambiguates equal-bbox balusters." };
            }
            finally
            {
                bool closed = destination.Close(false); Record("regression_baluster_document_closed", new { Closed = closed });
                if (!closed) throw new InvalidOperationException("Temporary generated-baluster regression document did not close without saving.");
            }
            return result;
        }

        void RunRegressions(UIApplication application)
        {
            if (config.Scenario != "diagnostic" || doc == null || !IsCopy(doc)) throw new InvalidOperationException("Regressions require an isolated diagnostic copy.");
            var cases = new List<object>(); var errors = new List<object>(); phase = "regressions";
            foreach (bool stamped in new[] { false, true })
                try { cases.Add(RegressionCoincidentGroup(application, stamped)); }
                catch (Exception ex) { errors.Add(new { Name = "coincident-group", Stamped = stamped, Error = ex.ToString() }); }
            try { cases.Add(RegressionGeneratedBalusters(application)); }
            catch (Exception ex) { errors.Add(new { Name = "generated-balusters", Error = ex.ToString() }); }
            foreach (var fixture in new[] { (520659L, false), (808923L, false), (926315L, false), (956337L, true), (930776L, true), (982660L, true), (982848L, true) })
                try { cases.Add(RegressionPlacement(application, fixture.Item1, fixture.Item2)); }
                catch (Exception ex) { errors.Add(new { Name = "placement", SourceId = fixture.Item1, Rotate = fixture.Item2, Error = ex.ToString() }); }
            var report = new { AtUtc = DateTime.UtcNow, Product = product.Location, ProductSha256 = Sha(product.Location), Cases = cases, Errors = errors, Passed = errors.Count == 0 && cases.Count == 10 && cases.All(c => (bool?)Read(c, "Passed") == true), Note = "True Revit helper regressions on temporary projects or transaction-group rollback copies. The generated-baluster case uses the actual six source elements; these fixtures do not replace end-to-end mass delete/restore or connected-network reconciliation." };
            Write("regressions.json", report); Record("regressions_complete", new { Cases = cases.Count, Errors = errors.Count, report.Passed }); phase = "regressions-complete";
        }
    }
}

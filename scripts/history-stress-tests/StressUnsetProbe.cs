using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.StressTests
{
    public sealed partial class StressObserver
    {
        void ProbeUnsetSystems(UIApplication application)
        {
            if (config.Scenario != "diagnostic" || doc == null || !IsCopy(doc)) throw new InvalidOperationException("Unset probes require an isolated diagnostic copy.");
            var targets = new[] { 604614L, 744177L, 744330L };
            var source = targets.Select(id => doc.GetElement(new ElementId(id))).Where(e => e != null).ToArray();
            var sourceBefore = source.ToDictionary(e => e.Id.Value, e => Capture(e));
            Write("unset-probe-source.json", new { AtUtc = DateTime.UtcNow, RequestedIds = targets, Sources = sourceBefore.Values.ToArray(), Note = "Exported before any rollback-only copy or setter attempt." });
            var cases = new List<object>(); var errors = new List<object>(); phase = "probe-unset-rollback";
            foreach (var original in source)
            {
                long? copyId = null; bool rolledBack = false; Spec copied = null, afterSet = null, afterCommit = null; object parameterBefore = null;
                string setError = null; bool? setReturn = null; string transactionError = null;
                using (var group = new TransactionGroup(doc, "BIMaestro diagnostic: unset copy rollback"))
                {
                    group.Start();
                    try
                    {
                        using var tx = new Transaction(doc, "BIMaestro diagnostic: native copied duct only"); tx.Start();
                        var copies = ElementTransformUtils.CopyElement(doc, original.Id, XYZ.Zero).Select(doc.GetElement).Where(e => e is MEPCurve).ToArray();
                        if (copies.Length != 1) throw new InvalidOperationException("Native copy did not return exactly one MEP curve.");
                        var copy = copies[0]; copyId = copy.Id.Value; doc.Regenerate(); copied = Capture(copy);
                        var parameter = copy.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
                        parameterBefore = parameter == null ? null : new { parameter.IsReadOnly, parameter.HasValue, Storage = parameter.StorageType.ToString(), Value = Safe(() => parameter.AsElementId()?.Value, (long?)null), Display = Safe(() => parameter.AsValueString()) };
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            try
                            {
                                if (parameter == null) throw new InvalidOperationException("RBS_DUCT_SYSTEM_TYPE_PARAM is absent on the native copy.");
                                setReturn = parameter.Set(ElementId.InvalidElementId); doc.Regenerate(); afterSet = Capture(copy);
                                sub.Commit();
                            }
                            catch (Exception ex) { setError = ex.ToString(); if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack(); }
                        }
                        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Diagnostic native-copy transaction did not commit before rollback.");
                        afterCommit = Capture(doc.GetElement(new ElementId(copyId.Value)));
                    }
                    catch (Exception ex) { transactionError = ex.ToString(); }
                    finally { if (group.GetStatus() == TransactionStatus.Started) rolledBack = group.RollBack() == TransactionStatus.RolledBack; }
                }
                var afterRollback = Capture(original); var before = sourceBefore[original.Id.Value];
                bool sourceUnchanged = before.Uid == afterRollback.Uid && before.SystemTypeUid == afterRollback.SystemTypeUid && Same(before.Point, afterRollback.Point)
                    && SamePoints(before.Curve, afterRollback.Curve) && SamePoints(before.BBox, afterRollback.BBox) && PortPlacementMatches(before, afterRollback);
                var result = new { SourceId = original.Id.Value, SourceBefore = before, CopiedBeforeSet = copied, ParameterBefore = parameterBefore, SetterReturn = setReturn, SetterException = setError, CopiedAfterSet = afterSet, CopiedAfterTransactionCommit = afterCommit, TransactionError = transactionError, RolledBack = rolledBack, CopyAbsentAfterRollback = !copyId.HasValue || doc.GetElement(new ElementId(copyId.Value)) == null, SourceAfterRollback = afterRollback, SourceUnchanged = sourceUnchanged };
                cases.Add(result); Write("unset-probe.json", new { AtUtc = DateTime.UtcNow, Cases = cases.ToArray(), Errors = errors.ToArray(), Complete = false });
                if (!rolledBack || !sourceUnchanged) errors.Add(new { SourceId = original.Id.Value, Reason = "Rollback or original-state verification failed." });
            }
            Write("unset-probe.json", new { AtUtc = DateTime.UtcNow, Product = product.Location, ProductSha256 = Sha(product.Location), Cases = cases.ToArray(), Errors = errors.ToArray(), Complete = true, Note = "The setter targets only new native copies within transaction groups that are always rolled back. A rejected setter is a diagnostic API outcome; it is not counted twice as a restoration failure. No shared MEP system was removed or edited and no file was saved." });
            Record("unset_probe_complete", new { Cases = cases.Count, Errors = errors.Count }); phase = "unset-probe-complete";
        }
    }
}

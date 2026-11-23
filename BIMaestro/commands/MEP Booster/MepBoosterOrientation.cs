using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Windows.Media.Media3D;
using BIMaestro.Localization;

namespace BIMaestro.MepBooster
{
    internal sealed class BoosterOrientationPlan
    {
        internal BoosterPart Part;
        internal double Angle;
        internal bool Flip;
        internal Transform PreviewTransform;
    }

    internal static class BoosterOrientation
    {
        private static Vector3D Vector(XYZ p) => new Vector3D(p.X, p.Y, p.Z);
        private static XYZ Basis(Transform t, int index) => index == 0 ? t.BasisX : index == 1 ? t.BasisY : t.BasisZ;

        private static BoosterPart Read(FamilyInstance instance)
        {
            if (instance == null || BoosterIds.Value(instance.Category?.Id) != (long)BuiltInCategory.OST_PipeAccessory)
                throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.CopyIsLimitedToStraightPipeAccessoriesNot"));
            if (instance.Mirrored || instance.Host != null)
                throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.CopyDoesNotYetSupportMirroredOrHosted"));
            var part = BoosterPart.Read(instance, false);
            if (part.Motion.Ports.Length != 2 || part.Motion.Ports.Any(p =>
                Math.Abs(Vector3D.DotProduct(p.Direction, part.Motion.Axis)) < 0.9999))
                throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.CopyRequiresTwoConnectorsAlignedOnThePipe"));
            return part;
        }

        internal static List<BoosterOrientationPlan> Plan(Document doc, ElementId referenceId, IList<ElementId> targets)
        {
            var reference = doc.GetElement(referenceId) as FamilyInstance;
            var source = Read(reference);
            var sourceTransform = reference.GetTransform();
            int radialIndex = Enumerable.Range(0, 3).OrderBy(i => Math.Abs(Basis(sourceTransform, i).DotProduct(source.Axis))).First();
            double roll = BoosterOrientationMath.Roll(Vector(source.Axis), Vector(Basis(sourceTransform, radialIndex)));
            var sourcePorts = BoosterPart.Ports(reference).OrderBy(p => p.Id).ToList();
            XYZ sourceDirection = (sourcePorts[1].Origin - sourcePorts[0].Origin).Normalize();
            XYZ localAxis = sourceTransform.Inverse.OfVector(sourceDirection);
            var plans = new List<BoosterOrientationPlan>();
            foreach (var id in targets)
            {
                var instance = doc.GetElement(id) as FamilyInstance;
                var part = Read(instance);
                if (reference.Id == id || instance.Symbol.Family.Id != reference.Symbol.Family.Id)
                    throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.TheReferenceMustBeOutsideTheSelectionAnd"));
                var ports = BoosterPart.Ports(instance).OrderBy(p => p.Id).ToList();
                XYZ direction = (ports[1].Origin - ports[0].Origin).Normalize();
                var transform = instance.GetTransform();
                if (localAxis.DotProduct(transform.Inverse.OfVector(direction)) < 0.9999)
                    throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.TheseTypesDoNotUseTheSameConnector"));
                bool flip = Math.Sign(sourceDirection.DotProduct(source.Axis)) != Math.Sign(direction.DotProduct(part.Axis));
                var inversion = flip ? part.Rotation(180, true) : Transform.Identity;
                XYZ radial = inversion.OfVector(Basis(transform, radialIndex));
                double angle = BoosterOrientationMath.Delta(roll, BoosterOrientationMath.Roll(Vector(part.Axis), Vector(radial)));
                if ((flip && !part.CanApply(180, true)) || !part.CanApply(angle, false))
                    throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.TheCopyWouldMoveAConnectedEndOf") + BoosterIds.Value(id) + UiLanguage.Get("MepBoosterOrientation.NoPartWillBeModified"));
                plans.Add(new BoosterOrientationPlan { Part = part, Flip = flip, Angle = angle,
                    PreviewTransform = part.Rotation(angle, false).Multiply(inversion) });
            }
            return plans;
        }

        internal sealed class TargetFilter : ISelectionFilter
        {
            private readonly ElementId _family;
            private readonly long _source;
            internal TargetFilter(ElementId family, ElementId source)
            { _family = family; _source = BoosterIds.Value(source); }
            public bool AllowElement(Element e) => e is FamilyInstance f && f.Symbol.Family.Id == _family
                && BoosterIds.Value(e.Category?.Id) == (long)BuiltInCategory.OST_PipeAccessory && _source != BoosterIds.Value(e.Id);
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        internal static void Apply(Document doc, IList<BoosterOrientationPlan> plans)
        {
            using (var group = new TransactionGroup(doc, "MEP Booster — copier l’orientation"))
            {
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.UnableToStartTheCopy"));
                try
                {
                    foreach (var plan in plans)
                    {
                        var original = ((FamilyInstance)doc.GetElement(plan.Part.Id)).GetTransform();
                        var expected = plan.PreviewTransform.Multiply(original);
                        if (plan.Flip) BoosterOperations.Apply(doc, new[] { plan.Part }, 180, true);
                        if (Math.Abs(plan.Angle) > 1e-7) BoosterOperations.Apply(doc, new[] { plan.Part }, plan.Angle, false);
                        var actual = ((FamilyInstance)doc.GetElement(plan.Part.Id)).GetTransform();
                        if (!actual.BasisX.IsAlmostEqualTo(expected.BasisX) || !actual.BasisY.IsAlmostEqualTo(expected.BasisY)
                            || !actual.BasisZ.IsAlmostEqualTo(expected.BasisZ) || actual.Origin.DistanceTo(expected.Origin) > 1e-5)
                            throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.TheResultDiffersFromThePreviewTheEntire"));
                    }
                    if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.TheCopyWasNotCommittedByRevit"));
                }
                catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
            }
        }

        internal static void Run(UIDocument uidoc, IList<ElementId> selection, BoosterPreview preview)
        {
            var doc = uidoc.Document;
            if (selection.Count != 1) throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.SelectASingleReferenceAccessoryBeforeStartingThe"));
            var referenceId = selection[0];
            var reference = doc.GetElement(referenceId) as FamilyInstance;
            Read(reference);
            var picked = uidoc.Selection.PickObjects(ObjectType.Element, new TargetFilter(reference.Symbol.Family.Id, referenceId),
                UiLanguage.Get("MepBoosterOrientation.MEPBoosterSelectTheTargetAccessoriesOfThe"));
            var targets = picked.Select(r => r.ElementId).GroupBy(BoosterIds.Value).Select(g => g.First()).ToList();
            if (targets.Count == 0) return;
            var plans = Plan(doc, referenceId, targets);
            var projection = BoosterProjection.Read(uidoc) ?? throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.ViewNotCompatibleWithThePreview"));
            var transforms = plans.ToDictionary(p => BoosterIds.Value(p.Part.Id), p => p.PreviewTransform);
            foreach (var plan in plans)
            {
                plan.Part.LoadEdges((FamilyInstance)doc.GetElement(plan.Part.Id), Math.Min(900, 5000 / plans.Count));
                if (plan.Part.Edges.Count == 0) throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.UnableToPrepareAReliablePreviewForAll"));
            }
            preview.Draw(projection, plans.Select(p => p.Part).ToList(), 0, false, p => transforms[BoosterIds.Value(p.Id)]);
            var confirm = new TaskDialog(UiLanguage.Get("MepBoosterOrientation.MEPBoosterCopyPreview"))
            {
                MainInstruction = UiLanguage.Get("MepBoosterOrientation.ReplicateOrientationAndDirectionOn") + plans.Count + UiLanguage.Get("MepBoosterOrientation.AccessoryIes"),
                MainContent = UiLanguage.Get("MepBoosterOrientation.TheGreenOutlineShowsTheFinalPosition")
                    + UiLanguage.Get("MepBoosterOrientation.TheInitiallySelectedReferenceRemainsUnchangedInstallati")
                    + UiLanguage.Get("MepBoosterOrientation.ReferenceProjectVerticalYAxisForVerticalPipes"),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };
            try
            {
                if (confirm.Show() != TaskDialogResult.Ok) return;
                var fresh = Plan(doc, referenceId, targets);
                for (int i = 0; i < plans.Count; i++)
                    if (fresh[i].Flip != plans[i].Flip || Math.Abs(fresh[i].Angle - plans[i].Angle) > 1e-7)
                        throw new InvalidOperationException(UiLanguage.Get("MepBoosterOrientation.TheOrientationHasChangedSinceThePreviewRestart"));
                Apply(doc, fresh);
                MepBoosterService.TutorialCopy?.Invoke(doc, referenceId,
                    fresh.Where(plan => plan.Flip || Math.Abs(plan.Angle) > 1e-7).Select(plan => plan.Part.Id));
            }
            finally { preview.Hide(); }
        }
    }
}

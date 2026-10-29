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
                throw new InvalidOperationException(UiLanguage.T("La copie est réservée aux accessoires de canalisation droits, pas aux tés ni aux coudes.", "Copy is limited to straight pipe accessories, not tees or elbows."));
            if (instance.Mirrored || instance.Host != null)
                throw new InvalidOperationException(UiLanguage.T("La copie ne prend pas encore en charge les accessoires en miroir ou hébergés.", "Copy does not yet support mirrored or hosted accessories."));
            var part = BoosterPart.Read(instance, false);
            if (part.Motion.Ports.Length != 2 || part.Motion.Ports.Any(p =>
                Math.Abs(Vector3D.DotProduct(p.Direction, part.Motion.Axis)) < 0.9999))
                throw new InvalidOperationException(UiLanguage.T("La copie nécessite deux connecteurs alignés sur l’axe du tuyau.", "Copy requires two connectors aligned on the pipe axis."));
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
                    throw new InvalidOperationException(UiLanguage.T("La référence doit être extérieure à la sélection et de la même famille que toutes les cibles.", "The reference must be outside the selection and of the same family as all targets."));
                var ports = BoosterPart.Ports(instance).OrderBy(p => p.Id).ToList();
                XYZ direction = (ports[1].Origin - ports[0].Origin).Normalize();
                var transform = instance.GetTransform();
                if (localAxis.DotProduct(transform.Inverse.OfVector(direction)) < 0.9999)
                    throw new InvalidOperationException(UiLanguage.T("Ces types n’utilisent pas le même repère de connecteurs : copie ambiguë, annulée.", "These types do not use the same connector reference frame: ambiguous copy, cancelled."));
                bool flip = Math.Sign(sourceDirection.DotProduct(source.Axis)) != Math.Sign(direction.DotProduct(part.Axis));
                var inversion = flip ? part.Rotation(180, true) : Transform.Identity;
                XYZ radial = inversion.OfVector(Basis(transform, radialIndex));
                double angle = BoosterOrientationMath.Delta(roll, BoosterOrientationMath.Roll(Vector(part.Axis), Vector(radial)));
                if ((flip && !part.CanApply(180, true)) || !part.CanApply(angle, false))
                    throw new InvalidOperationException(UiLanguage.T("La copie déplacerait une extrémité raccordée de l’accessoire ", "The copy would move a connected end of accessory ") + BoosterIds.Value(id) + UiLanguage.T(". Aucune pièce ne sera modifiée.", ". No part will be modified."));
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
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException(UiLanguage.T("Impossible de démarrer la copie.", "Unable to start the copy."));
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
                            throw new InvalidOperationException(UiLanguage.T("Le résultat diffère de l’aperçu : toute la copie a été annulée.", "The result differs from the preview: the entire copy was cancelled."));
                    }
                    if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException(UiLanguage.T("La copie n’a pas été validée par Revit.", "The copy was not committed by Revit."));
                }
                catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
            }
        }

        internal static void Run(UIDocument uidoc, IList<ElementId> selection, BoosterPreview preview)
        {
            var doc = uidoc.Document;
            if (selection.Count != 1) throw new InvalidOperationException(UiLanguage.T("Sélectionnez un seul accessoire de référence avant de lancer la copie.", "Select a single reference accessory before starting the copy."));
            var referenceId = selection[0];
            var reference = doc.GetElement(referenceId) as FamilyInstance;
            Read(reference);
            var picked = uidoc.Selection.PickObjects(ObjectType.Element, new TargetFilter(reference.Symbol.Family.Id, referenceId),
                UiLanguage.T("MEP Booster — sélectionnez les accessoires cibles de la même famille, puis Terminer. Orientation ET sens seront copiés. Échap pour annuler.", "MEP Booster — select the target accessories of the same family, then Finish. Orientation AND direction will be copied. Esc to cancel."));
            var targets = picked.Select(r => r.ElementId).GroupBy(BoosterIds.Value).Select(g => g.First()).ToList();
            if (targets.Count == 0) return;
            var plans = Plan(doc, referenceId, targets);
            var projection = BoosterProjection.Read(uidoc) ?? throw new InvalidOperationException(UiLanguage.T("Vue incompatible avec l’aperçu.", "View not compatible with the preview."));
            var transforms = plans.ToDictionary(p => BoosterIds.Value(p.Part.Id), p => p.PreviewTransform);
            foreach (var plan in plans)
            {
                plan.Part.LoadEdges((FamilyInstance)doc.GetElement(plan.Part.Id), Math.Min(900, 5000 / plans.Count));
                if (plan.Part.Edges.Count == 0) throw new InvalidOperationException(UiLanguage.T("Impossible de préparer un aperçu fiable pour toutes les pièces. Copie annulée.", "Unable to prepare a reliable preview for all parts. Copy cancelled."));
            }
            preview.Draw(projection, plans.Select(p => p.Part).ToList(), 0, false, p => transforms[BoosterIds.Value(p.Id)]);
            var confirm = new TaskDialog(UiLanguage.T("MEP Booster — aperçu de la copie", "MEP Booster — copy preview"))
            {
                MainInstruction = UiLanguage.T("Reproduire l’orientation et le sens sur ", "Replicate orientation and direction on ") + plans.Count + UiLanguage.T(" accessoire(s) ?", " accessory(ies)?"),
                MainContent = UiLanguage.T("La silhouette verte montre la position finale.\n", "The green outline shows the final position.\n")
                    + UiLanguage.T("La référence sélectionnée au départ reste inchangée.\nSens de montage inclus (repère des connecteurs, pas sens du fluide).", "The initially selected reference remains unchanged.\nInstallation direction included (connector reference frame, not flow direction).")
                    + UiLanguage.T("\nRepère : verticale du projet, axe Y pour les tuyaux verticaux.\nAucune canalisation ne doit être déplacée.", "\nReference: project vertical, Y axis for vertical pipes.\nNo pipework may be moved."),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };
            try
            {
                if (confirm.Show() != TaskDialogResult.Ok) return;
                var fresh = Plan(doc, referenceId, targets);
                for (int i = 0; i < plans.Count; i++)
                    if (fresh[i].Flip != plans[i].Flip || Math.Abs(fresh[i].Angle - plans[i].Angle) > 1e-7)
                        throw new InvalidOperationException(UiLanguage.T("L’orientation a changé depuis l’aperçu : recommencez la copie.", "The orientation has changed since the preview: restart the copy."));
                Apply(doc, fresh);
                MepBoosterService.TutorialCopy?.Invoke(doc, referenceId,
                    fresh.Where(plan => plan.Flip || Math.Abs(plan.Angle) > 1e-7).Select(plan => plan.Part.Id));
            }
            finally { preview.Hide(); }
        }
    }
}

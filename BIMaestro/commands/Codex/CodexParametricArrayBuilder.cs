using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    // Each network uses one nested, dimension-driven bar. Revit owns the associative
    // array and its count formula: changing the RFA later does not need the add-in.
    internal sealed class CodexParametricArrayBuilder
    {
        private readonly Document doc;
        private readonly ParametricArray spec;
        private readonly FamilySymbol symbol;
        private readonly ElementId symbolId;
        private readonly LinearArray array;
        private readonly FamilyInstance single;
        private readonly CodexParametricBuilder constraints;
        private static readonly string[] SizeNames = { "DimensionX", "DimensionY", "DimensionZ" };

        // Loading a family requires the destination to be outside a transaction.
        // These documents stay in memory; only the final host RFA is saved.
        internal static Dictionary<string, FamilySymbol> Prepare(Document host, string template, CodexParametricDesign design)
        {
            var result = new Dictionary<string, FamilySymbol>();
            foreach (var spec in design.Arrays)
            {
                Document child = null;
                try
                {
                    child = host.Application.NewFamilyDocument(template);
                    if (child == null) throw new InvalidOperationException("Document de barre non créé.");
                    var childDesign = new CodexParametricDesign();
                    var part = new ParametricPart { Name = "Barre", Material = "Barre", Axis = 2,
                        Min = new[] { new LengthExpression(), new LengthExpression(), new LengthExpression() },
                        Max = new LengthExpression[3] };
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double size = spec.Max[axis].Value(design.Initial) - spec.Min[axis].Value(design.Initial);
                        childDesign.Parameters.Add(new DrivingLength { Name = SizeNames[axis], Value = size, TestValue = size + Math.Max(1, size * 0.1) });
                        part.Max[axis] = new LengthExpression(); part.Max[axis].Terms.Add(SizeNames[axis], 1);
                    }
                    childDesign.Parts.Add(part);
                    var failures = new List<string>();
                    CodexParametricBuilder builder = null;
                    CodexAngularBarBuilder angularBuilder = null;
                    using (var transaction = new Transaction(child, "Construire la barre de référence"))
                    {
                        transaction.Start();
                        transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new CodexFamilyBuilder.Failures(failures)).SetClearAfterRollback(true));
                        var manager = child.FamilyManager;
                        if (manager.CurrentType == null) manager.NewType("Standard");
                        var material = manager.AddParameter("Materiau", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                        if (spec.RotationAxis < 0)
                        {
                            builder = new CodexParametricBuilder(child, childDesign);
                            builder.Build(new Dictionary<string, FamilyParameter> { ["Barre"] = material });
                        }
                        else angularBuilder = new CodexAngularBarBuilder(child, childDesign.Parameters.Select(p => p.Value).ToArray(), spec.RotationAxis, spec.Angle(design.Initial), material);
                        foreach (var plane in new FilteredElementCollector(child).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().Where(p => p.Name.StartsWith("BIM_Origine_", StringComparison.Ordinal)))
                            plane.get_Parameter(BuiltInParameter.ELEM_IS_REFERENCE).Set((int)FamilyInstanceReferenceType.StrongReference);
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(string.Join(" ; ", failures));
                    }
                    if (angularBuilder == null) builder.Flex();
                    else angularBuilder.Flex(design.Angles.Single(p => p.Name == spec.AngleParameter).TestValue);
                    using (var transaction = new Transaction(child, "Associer les options de la barre"))
                    {
                        transaction.Start();
                        var manager = child.FamilyManager;
                        foreach (var name in SizeNames.Concat(new[] { "Materiau", "Inclinaison" }))
                        { var p = manager.get_Parameter(name); if (p != null) manager.MakeInstance(p); }
                        var visible = manager.AddParameter("BIM_Visible", GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, true);
                        manager.Set(visible, 1);
                        var display = design.Registry?.Displays.FirstOrDefault(d => d.Component == spec.Name);
                        foreach (var form in new FilteredElementCollector(child).OfClass(typeof(GenericForm)).Cast<GenericForm>())
                        {
                            CodexParameterBuilder.SetDisplay(child, form, display);
                            manager.AssociateElementParameterToFamilyParameter(form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), visible);
                        }
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Options de la barre non validées.");
                    }
                    var loaded = child.LoadFamily(host);
                    if (loaded == null) throw new InvalidOperationException("Chargement de la barre imbriquée refusé.");
                    result.Add(spec.Name, loaded.GetFamilySymbolIds().Select(id => host.GetElement(id)).OfType<FamilySymbol>().First());
                }
                catch (Exception ex) { throw new InvalidOperationException("Préparation du réseau « " + spec.Name + " » : " + ex.Message, ex); }
                finally { if (child != null && child.IsValidObject) child.Close(false); }
            }
            return result;
        }

        internal CodexParametricArrayBuilder(Document doc, ParametricArray spec, FamilySymbol symbol, Dictionary<string, double> initial,
            FamilyParameter material, CodexParametricBuilder constraints)
        {
            this.doc = doc; this.spec = spec; this.symbol = symbol; symbolId = symbol.Id; this.constraints = constraints;
            string stage = "préparation";
            try
            {
                symbol.Family.Name = "BIMaestro_Barre_" + CodexFamilyBuilder.SafeName(spec.Name) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!symbol.IsActive) symbol.Activate();
                var manager = doc.FamilyManager;
                doc.Regenerate();
                var position = new XYZ(spec.Min[0].Value(initial), spec.Min[1].Value(initial), spec.Min[2].Value(initial)) / 304.8;
                var first = doc.FamilyCreate.NewFamilyInstance(position, symbol, StructuralType.NonStructural);
                for (int axis = 0; axis < 3; axis++)
                    constraints.AssociateLength(first.LookupParameter(SizeNames[axis]), LengthExpression.Combine(spec.Max[axis], spec.Min[axis], -1));
                if (spec.AngleParameter != null) constraints.AssociateAngle(first.LookupParameter("Inclinaison"), spec.AngleParameter);
                manager.AssociateElementParameterToFamilyParameter(first.LookupParameter("Materiau"), material);
                bool countInstance = spec.Span.Terms.Keys.Concat(spec.Pitch.Terms.Keys).Concat(new[] { spec.QuantityParameter }).Any(constraints.IsInstance);
                var count = manager.AddParameter(spec.CountParameter, GroupTypeId.Geometry, SpecTypeId.Int.Integer, countInstance);
                manager.SetFormula(count, spec.CountFormula);
                manager.SetDescription(count, "Nombre demandé. Pour 0/1, la représentation conditionnelle compatible Revit 2023 conserve des solides cachés.");
                FamilyParameter nativeCount = count;
                if (spec.SmallCounts)
                {
                    nativeCount = manager.AddParameter("BIM_Reseau_" + spec.CountParameter, GroupTypeId.Constraints, SpecTypeId.Int.Integer, countInstance);
                    manager.SetFormula(nativeCount, "if(" + spec.CountParameter + " < 2, 2, " + spec.CountParameter + ")");
                    string visible = constraints.VisibilityParameter(spec.Name);
                    bool visibleInstance = countInstance || (!string.IsNullOrEmpty(visible) && constraints.IsInstance(visible));
                    var groupVisible = manager.AddParameter("BIM_Groupe_" + spec.CountParameter, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, visibleInstance);
                    var singleVisible = manager.AddParameter("BIM_Unique_" + spec.CountParameter, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, visibleInstance);
                    string groupFormula = spec.CountParameter + " > 1", singleFormula = spec.CountParameter + " = 1";
                    if (!string.IsNullOrEmpty(visible)) { groupFormula = "and(" + visible + ", " + groupFormula + ")"; singleFormula = "and(" + visible + ", " + singleFormula + ")"; }
                    manager.SetFormula(groupVisible, groupFormula); manager.SetFormula(singleVisible, singleFormula);
                    manager.AssociateElementParameterToFamilyParameter(first.LookupParameter("BIM_Visible"), groupVisible);
                    single = doc.FamilyCreate.NewFamilyInstance(position, symbol, StructuralType.NonStructural);
                    for (int axis = 0; axis < 3; axis++) constraints.AssociateLength(single.LookupParameter(SizeNames[axis]), LengthExpression.Combine(spec.Max[axis], spec.Min[axis], -1));
                    if (spec.AngleParameter != null) constraints.AssociateAngle(single.LookupParameter("Inclinaison"), spec.AngleParameter);
                    manager.AssociateElementParameterToFamilyParameter(single.LookupParameter("Materiau"), material);
                    manager.AssociateElementParameterToFamilyParameter(single.LookupParameter("BIM_Visible"), singleVisible);
                    doc.Regenerate();
                    for (int axis = 0; axis < 3; axis++) Lock(() => single, axis, spec.Min[axis], constraints);
                }
                else constraints.AssociateVisibility(first.LookupParameter("BIM_Visible"), spec.Name);
                doc.Regenerate();
                stage = "création du réseau";
                array = LinearArray.Create(doc, constraints.DimensionView(spec.Axis), first.Id, Math.Max(2, spec.Count(initial)), Basis(spec.Axis) * (spec.Pitch.Value(initial) / 304.8), ArrayAnchorMember.Second);
                array.Label = nativeCount;
                doc.Regenerate();
                var members = Members().OrderBy(p => ((LocationPoint)p.Location).Point.DotProduct(Basis(spec.Axis))).ToArray();
                if (members.Length != Math.Max(2, spec.Count(initial))) throw new InvalidOperationException("Nombre de barres natives inattendu.");
                stage = "ancrage du premier membre";
                for (int axis = 0; axis < 3; axis++) Lock(() => OrderedMembers()[0], axis, spec.Min[axis], constraints);
                // The second member is the native anchor. Its offset drives the pitch,
                // while the integer label adds/removes subsequent members automatically.
                stage = "ancrage du pas";
                // Both anchors must follow transverse movements (e.g. -Largeur/2).
                // Leaving the second anchor free on those axes lets the array skew.
                for (int axis = 0; axis < 3; axis++)
                    Lock(() => OrderedMembers()[1], axis, axis == spec.Axis ? LengthExpression.Combine(spec.Min[axis], spec.Pitch) : spec.Min[axis], constraints);
                doc.Regenerate();
                stage = "vérification";
                Check(initial);
            }
            catch (Exception ex) { throw new InvalidOperationException("Réseau « " + spec.Name + " », " + stage + " : " + ex.Message, ex); }
        }
        private FamilyInstance[] OrderedMembers() => Members().OrderBy(p => ((LocationPoint)p.Location).Point.DotProduct(Basis(spec.Axis))).ToArray();
        private void Lock(Func<FamilyInstance> resolveMember, int axis, LengthExpression coordinate, CodexParametricBuilder constraints)
        {
            var plane = constraints.PlaneAt(axis, coordinate);
            doc.Regenerate();
            var reference = resolveMember().GetReferenceByName("BIM_Origine_" + "XYZ"[axis]);
            if (reference == null) throw new InvalidOperationException("Repère de la barre imbriquée inaccessible.");
            doc.FamilyCreate.NewAlignment(constraints.DimensionView(axis), plane.GetReference(), reference);
        }
        internal FamilyInstance[] Members()
        {
            return array.GetOriginalMemberIds().Concat(array.GetCopiedMemberIds()).SelectMany(id => Instances(doc.GetElement(id)))
                .Where(i => i.Symbol.Id.Equals(symbolId)).GroupBy(i => i.Id).Select(g => g.First()).ToArray();
        }
        internal IEnumerable<FamilyInstance> AllInstances() => Members().Concat(single == null ? new FamilyInstance[0] : new[] { single });
        private IEnumerable<FamilyInstance> Instances(Element element)
        {
            if (element is FamilyInstance instance) yield return instance;
            else if (element is Group group)
                foreach (var nested in group.GetMemberIds().SelectMany(id => Instances(doc.GetElement(id)))) yield return nested;
        }
        internal object Report(Dictionary<string, double> values) => new { name = spec.Name, count_parameter = spec.CountParameter,
            count = spec.Count(values), native_array_count = array.NumMembers, visible_count = constraints.Visible(spec.Name, values) ? spec.Count(values) : 0,
            physical_instances = array.NumMembers + (single == null ? 0 : 1),
            small_count_mode = spec.SmallCounts ? "Visibilité conditionnelle compatible 2023+ ; les géométries cachées restent présentes." : null,
            hidden_member_checks = "Paramètres, position et visibilité contrôlés. Revit ne renvoie pas les solides masqués ; volumes et orientations sont mesurés pour les membres visibles.",
            expected_count = spec.Count(values), pitch_mm = spec.Pitch.Value(values), span_mm = spec.Span.Value(values),
            rotation_axis = spec.RotationAxis < 0 ? null : "xyz"[spec.RotationAxis].ToString(), angle_parameter = spec.AngleParameter, angle_deg = spec.Angle(values) };

        internal void Check(Dictionary<string, double> values)
        {
            var networkMembers = Members().OrderBy(p => ((LocationPoint)p.Location).Point.DotProduct(Basis(spec.Axis))).ToArray();
            if (array.NumMembers != Math.Max(2, spec.Count(values)) || networkMembers.Length != Math.Max(2, spec.Count(values))) throw new InvalidOperationException("Le nombre de barres du réseau « " + spec.Name + " » ne suit pas sa formule.");
            var members = networkMembers.Concat(single == null ? new FamilyInstance[0] : new[] { single }).ToArray();
            for (int index = 0; index < members.Length; index++)
            {
                bool isSingle = single != null && members[index].Id.Equals(single.Id);
                bool expectedVisible = constraints.Visible(spec.Name, values) && (!spec.SmallCounts || (isSingle ? spec.Count(values) == 1 : spec.Count(values) > 1));
                if (members[index].LookupParameter("BIM_Visible").AsInteger() != (expectedVisible ? 1 : 0)) throw new InvalidOperationException("Visibilité du réseau incorrecte : " + spec.Name);
                var location = ((LocationPoint)members[index].Location).Point;
                for (int a = 0; a < 3; a++)
                {
                    double size = members[index].LookupParameter(SizeNames[a]).AsDouble() * 304.8;
                    double coordinate = spec.Min[a].Value(values) + (a == spec.Axis && !isSingle ? index * spec.Pitch.Value(values) : 0);
                    if (Math.Abs(size - (spec.Max[a].Value(values) - spec.Min[a].Value(values))) > 0.5 || Math.Abs(location.DotProduct(Basis(a)) * 304.8 - coordinate) > 0.5)
                        throw new InvalidOperationException("Dimensions ou position native incorrectes : " + spec.Name + ", membre " + (index + 1) + ", axe " + "XYZ"[a] +
                            ", dimension " + size.ToString("G8") + " / attendue " + (spec.Max[a].Value(values) - spec.Min[a].Value(values)).ToString("G8") +
                            ", position " + (location.DotProduct(Basis(a)) * 304.8).ToString("G8") + " / attendue " + coordinate.ToString("G8") + " mm.");
                }
                if (spec.RotationAxis >= 0 && Math.Abs(members[index].LookupParameter("Inclinaison").AsDouble() * 180 / Math.PI - spec.Angle(values)) > 1e-6)
                    throw new InvalidOperationException("Angle natif incorrect : " + spec.Name);
                if (!expectedVisible) continue;
                var box = SolidBounds(members[index]);
                if (box == null) throw new InvalidOperationException("Encombrement de barre absent.");
                var points = Enumerable.Range(0, 8).Select(c => box.Transform.OfPoint(new XYZ((c & 1) == 0 ? box.Min.X : box.Max.X,
                    (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z))).ToArray();
                double volume = 1;
                var expected = spec.Corners(values, isSingle ? 0 : index);
                for (int axis = 0; axis < 3; axis++)
                {
                    double min = expected.Min(p => p[axis]), max = expected.Max(p => p[axis]);
                    if (Math.Abs(points.Min(p => p.DotProduct(Basis(axis))) * 304.8 - min) > 0.5 || Math.Abs(points.Max(p => p.DotProduct(Basis(axis))) * 304.8 - max) > 0.5)
                        throw new InvalidOperationException("Position, pas ou section incorrects pour la barre " + (index + 1) + " du réseau « " + spec.Name + " ».");
                    volume *= spec.Max[axis].Value(values) - spec.Min[axis].Value(values);
                }
                using (var geometry = members[index].get_Geometry(new Options { IncludeNonVisibleObjects = true }))
                {
                    if (Math.Abs(Volume(geometry) * Math.Pow(304.8, 3) - volume) > Math.Max(1, volume * 0.001)) throw new InvalidOperationException("Volume incorrect dans le réseau « " + spec.Name + " ».");
                    if (spec.RotationAxis >= 0) CodexAngularBarBuilder.CheckNormals(geometry, spec.RotationAxis, spec.Angle(values));
                }
            }
        }
        private static double Volume(GeometryElement geometry)
        {
            double result = 0;
            foreach (var item in geometry)
            {
                if (item is Solid solid) result += solid.Volume;
                else if (item is GeometryInstance instance) using (var nested = instance.GetInstanceGeometry()) result += Volume(nested);
            }
            return result;
        }
        internal static BoundingBoxXYZ SolidBounds(Element element)
        {
            using (var geometry = element.get_Geometry(new Options { IncludeNonVisibleObjects = true }))
            {
                var points = SolidPoints(geometry).ToArray();
                if (points.Length == 0) throw new InvalidOperationException("La barre imbriquée ne contient aucun solide mesurable.");
                return new BoundingBoxXYZ { Min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                    Max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)) };
            }
        }
        private static IEnumerable<XYZ> SolidPoints(GeometryElement geometry)
        {
            foreach (var item in geometry)
                if (item is Solid solid && solid.Volume > 0)
                {
                    foreach (var edge in solid.Edges.Cast<Edge>())
                        using (var curve = edge.AsCurve()) foreach (var point in curve.Tessellate()) yield return point;
                }
                else if (item is GeometryInstance instance)
                    using (var nested = instance.GetInstanceGeometry()) foreach (var point in SolidPoints(nested)) yield return point;
        }
        private static XYZ Basis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
    }
}

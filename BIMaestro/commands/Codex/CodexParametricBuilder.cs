using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class CodexParametricBuilder
    {
        private readonly Document doc;
        private readonly CodexParametricDesign design;
        private readonly FamilyManager manager;
        private readonly Dictionary<string, FamilyParameter> drivers = new Dictionary<string, FamilyParameter>();
        private readonly Dictionary<string, ReferencePlane> planes = new Dictionary<string, ReferencePlane>();
        private readonly Dictionary<string, FamilyParameter> calculatedLengths = new Dictionary<string, FamilyParameter>();
        private readonly List<CodexParametricArrayBuilder> arrays = new List<CodexParametricArrayBuilder>();
        private readonly ReferencePlane[] origins = new ReferencePlane[3];
        private readonly View[] views = new View[3]; // views looking along X, Y, Z
        private readonly List<Extrusion> extrusions = new List<Extrusion>();
        private int sequence;
        private CodexParameterBuilder registryBuilder;
        private CodexConnectorBuilder connectorBuilder;
        private CodexSymbolicBuilder symbolicBuilder;
        internal CodexHostOpeningBuilder HostOpening { get; private set; }

        internal CodexParametricBuilder(Document doc, CodexParametricDesign design)
        {
            this.doc = doc; this.design = design; manager = doc.FamilyManager;
            var available = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate &&
                (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Section)).ToList();
            for (int a = 0; a < 3; a++)
            {
                views[a] = available.FirstOrDefault(v => Math.Abs(v.ViewDirection.DotProduct(Basis(a))) > 0.999);
                if (views[a] == null) throw new InvalidOperationException("Le gabarit doit contenir un plan et des élévations selon X et Y pour créer les contraintes.");
            }
        }

        internal List<Element> Build(Dictionary<string, FamilyParameter> materials, Dictionary<string, FamilySymbol> prototypes = null)
        {
            foreach (var step in BuildSteps(materials, prototypes)) step();
            return CreatedElements();
        }
        internal List<Element> CreatedElements() => extrusions.Cast<Element>().Concat(arrays.SelectMany(a => a.AllInstances()).Cast<Element>()).ToList();
        internal IEnumerable<Action> BuildSteps(Dictionary<string, FamilyParameter> materials, Dictionary<string, FamilySymbol> prototypes = null)
        {
            yield return () =>
            {
                if (design.Registry != null)
                {
                    registryBuilder = new CodexParameterBuilder(doc, design.Registry);
                    foreach (var pair in registryBuilder.Parameters) drivers.Add(pair.Key, pair.Value);
                }
                for (int a = 0; a < 3; a++) { origins[a] = NewPlane(a, 0, "BIM_Origine_" + "XYZ"[a]); origins[a].Pinned = true; }
                foreach (var parameter in design.Parameters)
                {
                    var native = registryBuilder != null ? drivers[parameter.Name] : CodexParameterBuilder.GetOrAdd(manager, parameter.Name, GroupTypeId.Geometry, SpecTypeId.Length, false);
                    if (registryBuilder == null) { manager.Set(native, Feet(parameter.Value)); RegisterDriver(parameter.Name, native); CodexParameterBuilder.Describe(manager, native, "Réglage de la famille, en millimètres."); }
                    // A user-facing dimension labeled by the actual driving parameter.
                    int axis = Enumerable.Range(0, 3).OrderByDescending(a => design.Parts.Count(p => p.Min[a].Terms.ContainsKey(parameter.Name) || p.Max[a].Terms.ContainsKey(parameter.Name))).First();
                    var end = NewPlane(axis, Feet(parameter.Value), "BIM_Repere_" + parameter.Name);
                    sequence++;
                    doc.Regenerate(); Label(axis, end, Feet(parameter.Value), native);
                    var expression = new LengthExpression(); expression.Terms.Add(parameter.Name, 1);
                    planes[axis + ":" + expression.Formula()] = end;
                }
            };
            foreach (var part in design.Parts)
            {
                yield return () =>
                {
                    CodexCreationGuard.Check("construction de « " + part.Name + " »");
                    try
                    {
                        var min = part.Min.Select(e => Feet(e.Value(design.Initial))).ToArray();
                        var max = part.Max.Select(e => Feet(e.Value(design.Initial))).ToArray();
                        int[] uv = part.ProfileAxes;
                        var profile = new CurveArrArray();
                        profile.Append(part.Profile == null ? Rectangle(part.Axis, min[part.Axis], min[uv[0]], min[uv[1]], max[uv[0]], max[uv[1]], false) : CodexProfileBuilder.Profile(part, design.Initial));
                        foreach (var hole in part.Openings)
                            profile.Append(Rectangle(part.Axis, min[part.Axis], Feet(hole.Min[0].Value(design.Initial)), Feet(hole.Min[1].Value(design.Initial)),
                                Feet(hole.Max[0].Value(design.Initial)), Feet(hole.Max[1].Value(design.Initial)), true));
                        // Associative work plane: movement of the minimum plane moves the sketch.
                        var workPlane = SketchPlane.Create(doc, PlaneAt(part.Axis, part.Min[part.Axis]).Id);
                        var extrusion = doc.FamilyCreate.NewExtrusion(true, profile, workPlane, max[part.Axis] - min[part.Axis]);
                        AssociateLength(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), LengthExpression.Combine(part.Max[part.Axis], part.Min[part.Axis], -1));
                        manager.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), materials[part.Material]);
                        extrusion.Subcategory = doc.Settings.Categories.NewSubcategory(doc.OwnerFamily.FamilyCategory, CodexFamilyBuilder.SafeName(part.Name));
                        extrusion.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(part.Name);
                        doc.Regenerate();
                        var boundaries = new List<Tuple<int, LengthExpression>>();
                        foreach (int a in uv) { boundaries.Add(Tuple.Create(a, part.Min[a])); boundaries.Add(Tuple.Create(a, part.Max[a])); }
                        foreach (var hole in part.Openings)
                            for (int a = 0; a < 2; a++) { boundaries.Add(Tuple.Create(uv[a], hole.Min[a])); boundaries.Add(Tuple.Create(uv[a], hole.Max[a])); }
                        if (part.Profile != null) { CodexProfileBuilder.Constrain(doc, extrusion, part, design.Initial, this); boundaries.Clear(); }
                        foreach (var boundary in boundaries.GroupBy(b => b.Item1 + ":" + b.Item2.Formula()).Select(g => g.First()))
                        {
                            var plane = PlaneAt(boundary.Item1, boundary.Item2);
                            doc.Regenerate();
                            using (var geometry = extrusion.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true }))
                            {
                                var faces = geometry.OfType<Solid>().SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>()
                                    .Where(f => Math.Abs(f.FaceNormal.DotProduct(Basis(boundary.Item1))) > 0.999999 &&
                                        Math.Abs(f.Origin.DotProduct(Basis(boundary.Item1)) - Feet(boundary.Item2.Value(design.Initial))) < 1e-6).ToList();
                                if (faces.Count == 0) throw new InvalidOperationException("Face de référence introuvable pour " + boundary.Item2.Formula());
                                foreach (var face in faces)
                                {
                                    if (face.Reference == null) throw new InvalidOperationException("Référence de face absente.");
                                    doc.FamilyCreate.NewAlignment(views[part.Axis], plane.GetReference(), face.Reference);
                                }
                            }
                        }
                        registryBuilder?.Display(extrusion, part.Name);
                        extrusions.Add(extrusion);
                    }
                    catch (Exception ex) { throw new InvalidOperationException("Contraintes de « " + part.Name + " » : " + ex.Message, ex); }
                };
            }
            yield return () =>
            {
                foreach (var parameter in design.Angles)
                {
                    if (registryBuilder != null) continue;
                    var native = CodexParameterBuilder.GetOrAdd(manager, parameter.Name, GroupTypeId.Geometry, SpecTypeId.Angle, false);
                    manager.Set(native, parameter.Value * Math.PI / 180); RegisterDriver(parameter.Name, native);
                    CodexParameterBuilder.Describe(manager, native, "Inclinaison des éléments répétés, en degrés. Plage testée : 0 à 180 degrés.");
                }
            };
            foreach (var spec in design.Arrays)
            {
                yield return () =>
                {
                    CodexCreationGuard.Check("réseau « " + spec.Name + " »");
                    if (prototypes == null || !prototypes.TryGetValue(spec.Name, out var symbol)) throw new InvalidOperationException("Barre imbriquée absente : " + spec.Name);
                    arrays.Add(new CodexParametricArrayBuilder(doc, spec, symbol, design.Initial, materials[spec.Material], this, materials));
                };
            }
            yield return () =>
            {
                connectorBuilder = new CodexConnectorBuilder(doc, design, extrusions, this);
                symbolicBuilder = new CodexSymbolicBuilder(doc, design, this);
                if (design.Metadata?.HostOpening != null) HostOpening = new CodexHostOpeningBuilder(doc, design.Metadata.HostOpening, design.Initial, this);
                doc.Regenerate(); Check(design.Initial);
            };
        }

        internal void AssociateLength(Parameter target, LengthExpression expression, bool constrainConstant = false)
        {
            if (target == null || target.IsReadOnly) throw new InvalidOperationException("Paramètre de longueur cible inaccessible.");
            if (expression.Terms.Count == 0 && !constrainConstant) target.Set(Feet(expression.Offset));
            else manager.AssociateElementParameterToFamilyParameter(target, LengthParameter(expression));
        }
        internal void AssociateAngle(Parameter target, string parameterName)
        {
            if (target == null || target.IsReadOnly) throw new InvalidOperationException("Paramètre d'angle imbriqué inaccessible.");
            manager.AssociateElementParameterToFamilyParameter(target, drivers[parameterName]);
        }
        private FamilyParameter LengthParameter(LengthExpression expression)
        {
            if (expression.Offset == 0 && expression.Terms.Count == 1 && expression.Terms.First().Value == 1) return drivers[expression.Terms.First().Key];
            string formula = expression.Formula();
            if (calculatedLengths.TryGetValue(formula, out var existing)) return existing;
            var parameter = CodexParameterBuilder.NewInternal(manager, "BIM_Calcul_" + (++sequence), GroupTypeId.Constraints, SpecTypeId.Length, expression.Terms.Keys.Any(IsInstance));
            manager.SetFormula(parameter, NativeFormula(formula));
            manager.SetDescription(parameter, "Calcul interne partagé : " + formula + ". Modifier les réglages de la famille plutôt que cette formule.");
            calculatedLengths.Add(formula, parameter); return parameter;
        }
        internal string NativeFormula(string formula) => CodexParameterBuilder.NativeFormula(formula, drivers);
        internal void RegisterDriver(string name, FamilyParameter parameter)
        {
            if (drivers.Values.Any(p => p.Id == parameter.Id)) throw new InvalidOperationException("Deux réglages désignent le même paramètre natif : " + name);
            drivers.Add(name, parameter);
        }
        internal object[] ArrayReports(Dictionary<string, double> values) => arrays.Select(a => a.Report(values)).ToArray();
        internal int InternalLengthCount => calculatedLengths.Count;
        internal object[] ConnectorReports() => connectorBuilder?.Report() ?? new object[0];

        // Called after the construction transaction has COMMITTED. Every test commits as well,
        // so deferred Revit constraint failures cannot be mistaken for successful regeneration.
        internal object[] Flex() => FlexSteps().Where(x => x != null).ToArray();
        internal IEnumerable<object> FlexSteps()
        {
            if (registryBuilder != null)
            {
                foreach (var report in FlexRegistry()) yield return report;
                yield break;
            }
            foreach (var values in design.TestCases().Skip(1))
            {
                CodexCreationGuard.Check("test de variation");
                Apply(values);
                yield return new { values_mm = values.Where(p => !design.Angles.Any(a => a.Name == p.Key)).ToDictionary(p => p.Key, p => p.Value),
                    values_deg = design.Angles.ToDictionary(p => p.Name, p => values[p.Name]), arrays = ArrayReports(values), verified = true };
                Apply(design.Initial);
                yield return null;
            }
        }
        private IEnumerable<object> FlexRegistry()
        {
            foreach (var test in design.Registry.Cases())
            {
                CodexCreationGuard.Check("test « " + test.Name + " »");
                ApplyRegistry(test);
                yield return new { scenario = test.Name, type = test.TypeName, verified = true, arrays = ArrayReports(test.Numeric) };
                var type = design.Registry.Types.Single(t => t.Name == test.TypeName);
                ApplyRegistry(new FamilyCase { Name = "Restaurer " + type.Name, TypeName = type.Name, Values = design.Registry.Evaluate(type.Overrides) });
                yield return null;
            }
            ApplyRegistry(new FamilyCase { Name = "Restaurer le type initial", TypeName = design.Registry.Types[0].Name, Values = design.Registry.Initial });
            yield return null;
        }
        private void ApplyRegistry(FamilyCase test)
        {
            var failures = new List<string>();
            using (var transaction = new Transaction(doc, "BIMaestro — " + test.Name))
            {
                transaction.Start();
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new CodexFamilyBuilder.Failures(failures)).SetClearAfterRollback(true));
                try
                {
                    registryBuilder.Apply(test); doc.Regenerate(); registryBuilder.Check(test); Check(test.Numeric);
                    if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(string.Join(" ; ", failures));
                    CodexCreationGuard.Check();
                    registryBuilder.Check(test); Check(test.Numeric);
                }
                catch (Exception ex) { throw new InvalidOperationException("Échec du scénario « " + test.Name + " » : " + ex.Message, ex); }
            }
        }
        internal bool IsInstance(string name) => design.Registry?.Parameters.Any(p => p.Name == name && p.Instance) == true;
        internal void AssociateVisibility(Parameter target, string component)
        {
            string name = design.Registry?.Displays.FirstOrDefault(d => d.Component == component)?.VisibleParameter;
            if (!string.IsNullOrEmpty(name)) manager.AssociateElementParameterToFamilyParameter(target, drivers[name]);
        }
        internal void AssociateBoolean(Parameter target, string name)
        {
            if (target == null || target.IsReadOnly) throw new InvalidOperationException("Visibilité paramétrique inaccessible sur cet élément.");
            manager.AssociateElementParameterToFamilyParameter(target, drivers[name]);
        }
        internal string VisibilityParameter(string component) => design.Registry?.Displays.FirstOrDefault(d => d.Component == component)?.VisibleParameter;
        internal bool Visible(string component, Dictionary<string, double> values) => string.IsNullOrEmpty(VisibilityParameter(component)) || values[VisibilityParameter(component)] != 0;
        internal View ProfileView(int axis) => views[axis];
        private void Apply(Dictionary<string, double> values)
        {
            var failures = new List<string>();
            using (var transaction = new Transaction(doc, "BIMaestro — test de flexion"))
            {
                transaction.Start();
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new CodexFamilyBuilder.Failures(failures)).SetClearAfterRollback(true));
                try
                {
                    foreach (var p in values) manager.Set(drivers[p.Key], design.Angles.Any(a => a.Name == p.Key) ? p.Value * Math.PI / 180 : Feet(p.Value));
                    doc.Regenerate(); Check(values);
                    if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(string.Join(" ; ", failures));
                    CodexCreationGuard.Check();
                    Check(values);
                }
                catch (Exception ex) { throw new InvalidOperationException("Test paramétrique [" + string.Join(", ", values.Select(p => p.Key + "=" + p.Value + (design.Angles.Any(a => a.Name == p.Key) ? " degrés" : " mm"))) + "] : " + ex.Message, ex); }
            }
        }
        private void Check(Dictionary<string, double> values)
        {
            design.ValidateAt(values);
            connectorBuilder?.Check(values);
            symbolicBuilder?.Check(values);
            HostOpening?.Check(values);
            foreach (var array in arrays) array.Check(values);
            for (int i = 0; i < extrusions.Count; i++)
            {
                var part = design.Parts[i];
                registryBuilder?.CheckDisplay(extrusions[i], part.Name, values);
                var box = extrusions[i].get_BoundingBox(null);
                if (box == null) throw new InvalidOperationException("Encombrement absent : " + part.Name);
                var corners = Enumerable.Range(0, 8).Select(c => box.Transform.OfPoint(new XYZ((c & 1) == 0 ? box.Min.X : box.Max.X,
                    (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z))).ToArray();
                for (int a = 0; a < 3; a++)
                    if (Math.Abs(corners.Min(p => p.DotProduct(Basis(a))) * 304.8 - part.Min[a].Value(values)) > 0.5 ||
                        Math.Abs(corners.Max(p => p.DotProduct(Basis(a))) * 304.8 - part.Max[a].Value(values)) > 0.5)
                        throw new InvalidOperationException("La pièce « " + part.Name + " » ne suit pas sa cote " + "XYZ"[a] + ".");
                // Volume checks also detect a frozen opening even if the outer dimensions flex correctly.
                int[] uv = part.ProfileAxes;
                double area = (part.Max[uv[0]].Value(values) - part.Min[uv[0]].Value(values)) * (part.Max[uv[1]].Value(values) - part.Min[uv[1]].Value(values));
                if (part.Profile != null) { area = CodexProfileDesign.Area(part.Profile.Select(p => p.Select(e => e.Value(values)).ToArray()).ToArray()); CodexProfileBuilder.Check(extrusions[i], part, values); }
                area -= part.Openings.Sum(h => (h.Max[0].Value(values) - h.Min[0].Value(values)) * (h.Max[1].Value(values) - h.Min[1].Value(values)));
                double expected = area * (part.Max[part.Axis].Value(values) - part.Min[part.Axis].Value(values));
                using (var geometry = extrusions[i].get_Geometry(new Options { IncludeNonVisibleObjects = true }))
                {
                    double actual = geometry.OfType<Solid>().Sum(s => s.Volume) * Math.Pow(304.8, 3);
                    if (Math.Abs(actual - expected) > Math.Max(1, expected * 0.001)) throw new InvalidOperationException("Le volume ou les ouvertures de « " + part.Name + " » ne suivent pas les paramètres.");
                    var faces = geometry.OfType<Solid>().SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>().ToArray();
                    foreach (var opening in part.Openings)
                        for (int a = 0; a < 2; a++)
                            foreach (var boundary in new[] { opening.Min[a], opening.Max[a] })
                                if (!faces.Any(f => Math.Abs(f.FaceNormal.DotProduct(Basis(uv[a]))) > 0.999 &&
                                    Math.Abs(f.Origin.DotProduct(Basis(uv[a])) * 304.8 - boundary.Value(values)) < 0.5))
                                    throw new InvalidOperationException("Une ouverture de « " + part.Name + " » ne suit pas sa position paramétrique.");
                }
            }
        }
        internal ReferencePlane PlaneAt(int axis, LengthExpression expression)
        {
            if (expression.IsZero) return origins[axis];
            string key = axis + ":" + expression.Formula();
            if (planes.TryGetValue(key, out var cached)) return cached;
            double value = Feet(expression.Value(design.Initial));
            var plane = NewPlane(axis, value, "BIM_" + "XYZ"[axis] + "_" + (++sequence));
            if (expression.Terms.Count == 0) plane.Pinned = true;
            else { var parameter = LengthParameter(expression.Scaled(Math.Sign(value))); doc.Regenerate(); Label(axis, plane, value, parameter); }
            planes.Add(key, plane); return plane;
        }
        internal View DimensionView(int axis) => axis == 2 ? views[1] : views[2];
        private ReferencePlane NewPlane(int axis, double value, string name)
        {
            XYZ a, b, cut;
            if (axis == 0) { a = new XYZ(value, -2, 0); b = new XYZ(value, 2, 0); cut = XYZ.BasisZ; }
            else if (axis == 1) { a = new XYZ(-2, value, 0); b = new XYZ(2, value, 0); cut = XYZ.BasisZ; }
            else { a = new XYZ(-2, 0, value); b = new XYZ(2, 0, value); cut = XYZ.BasisY; }
            var view = axis == 2 ? views[1] : views[2];
            var plane = doc.FamilyCreate.NewReferencePlane(a, b, cut, view);
            if (plane.GetPlane().Normal.DotProduct(Basis(axis)) < 0)
            {
                doc.Delete(plane.Id); plane = doc.FamilyCreate.NewReferencePlane(b, a, cut, view);
            }
            if (plane.GetPlane().Normal.DotProduct(Basis(axis)) < 0.999) throw new InvalidOperationException("Orientation du plan de référence incorrecte.");
            plane.Name = name; return plane;
        }
        private void Label(int axis, ReferencePlane plane, double value, FamilyParameter parameter)
        {
            var references = new ReferenceArray(); references.Append(origins[axis].GetReference()); references.Append(plane.GetReference());
            var offset = (axis == 0 ? XYZ.BasisY : XYZ.BasisX) * (2 + sequence * 0.02);
            using (var line = Line.CreateBound(offset, offset + Basis(axis) * Math.Sign(value) * Math.Max(Math.Abs(value), 10 / 304.8)))
                doc.FamilyCreate.NewLinearDimension(axis == 2 ? views[1] : views[2], line, references).FamilyLabel = parameter;
        }
        private static CurveArray Rectangle(int axis, double origin, double u0, double v0, double u1, double v1, bool reverse)
        {
            int[] uv = Enumerable.Range(0, 3).Where(a => a != axis).ToArray();
            var points = new[] { new[] { u0, v0 }, new[] { u1, v0 }, new[] { u1, v1 }, new[] { u0, v1 } }
                .Select(p => Basis(axis) * origin + Basis(uv[0]) * p[0] + Basis(uv[1]) * p[1]).ToArray();
            if (reverse) Array.Reverse(points);
            var profile = new CurveArray();
            for (int i = 0; i < 4; i++) profile.Append(Line.CreateBound(points[i], points[(i + 1) % 4]));
            return profile;
        }
        private static XYZ Basis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
        private static double Feet(double mm) => mm / 304.8;
    }
}

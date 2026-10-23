using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal static class MaintenanceCorrectionMemory
    {
        private const string ReuseWarning = "Correction mémorisée réutilisée sur une géométrie compatible : repères, sens et cotes restent à vérifier dans ce projet.";
        private const string AssumedCourseWarning = "Course de manœuvre mémorisée sans justificatif constructeur : elle reste une hypothèse à confirmer.";
        private sealed class Identity
        {
            internal string Signature, Geometry, Parameters, Connectors, Type, Family;
            internal Transform Source;
        }

        // All entry points fail closed. A missing/corrupt/unsupported memory
        // must never stop ordinary recognition or maintenance analysis.
        internal static bool TryApply(Document doc, FamilyInstance source, MaintenanceWorkbenchState state, out string note)
        {
            note = "";
            try
            {
                if (state == null) throw new ArgumentException("État de l’intervention absent.");
                string path = MaintenanceCorrectionData.FilePath();
                var data = MaintenanceCorrectionData.Read(path);
                if (data.Corrections.Count == 0) return false;
                Identity identity = Capture(doc, source);
                string requested = state.Operation.ToString();
                var records = data.Corrections.Where(r => r.Signature == identity.Signature && (r.Operation == requested
                    || state.Operation == MaintenanceAccessOperation.HandwheelValve
                        && (r.Operation == "LeverValve" || r.Operation == "ChainwheelValve"))).ToArray();
                if (records.Length == 0)
                {
                    note = "Aucune correction compatible avec ce type et sa géométrie actuelle. Les variantes ne sont pas redimensionnées automatiquement.";
                    return false;
                }
                if (records.Length != 1)
                { note = "Plusieurs gestes mémorisés sont compatibles : choisissez l’intervention avant de réutiliser une correction."; return false; }
                var record = records[0];
                XYZ command = identity.Source.OfPoint(Point(record.Command));
                XYZ x = identity.Source.OfVector(Point(record.X)).Normalize();
                XYZ z = identity.Source.OfVector(Point(record.Z)).Normalize();
                XYZ rawY = identity.Source.OfVector(Point(record.Y)).Normalize();
                XYZ y = z.CrossProduct(x).Normalize();
                double handedness = y.DotProduct(rawY) < 0 ? -1 : 1;
                var frame = Transform.Identity; frame.Origin = command; frame.BasisX = x; frame.BasisY = y; frame.BasisZ = z;
                ValidateFrame(frame);
                var fields = record.Fields.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                // Reflection changes the handedness of the frame. Rebuild a
                // right-handed maintenance frame AND mirror signed coordinates.
                // This preserves the actual initial handle direction and its
                // trajectory, including a filter's off-centre removal volume.
                fields["InitialAngle"] *= handedness; fields["Angle"] *= handedness; fields["OffsetY"] *= handedness;
                foreach (var field in fields)
                    state.Fields[field.Key] = (IsAngle(field.Key) ? field.Value : field.Value * 304.8).ToString("R", CultureInfo.CurrentCulture);
                state.Operation = (MaintenanceAccessOperation)Enum.Parse(typeof(MaintenanceAccessOperation), record.Operation);
                state.CommandPoint = command; state.DetectedFrame = frame; state.Axis = 6;
                state.Measured = record.Measured; state.Manufacturer = record.Manufacturer && !string.IsNullOrWhiteSpace(record.Notice);
                state.RotationAssumed = record.RotationAssumed;
                state.Notice = record.Notice;
                state.FeatureWarnings.Clear(); state.FeatureWarnings.AddRange(record.Warnings.Distinct(StringComparer.Ordinal));
                if (!state.FeatureWarnings.Contains(ReuseWarning)) state.FeatureWarnings.Add(ReuseWarning);
                if (!state.Manufacturer && state.Operation != MaintenanceAccessOperation.Filter && state.Operation != MaintenanceAccessOperation.Actuator)
                    if (!state.FeatureWarnings.Contains(AssumedCourseWarning)) state.FeatureWarnings.Add(AssumedCourseWarning);
                state.DetectionNote = "Correction mémorisée · géométrie, connecteurs et paramètres compatibles · " + record.SavedUtc;
                ClearResults(state);
                note = "Correction mémorisée appliquée ; le poste et les collisions doivent être recalculés. Aucune validation précédente n’est reprise.";
                return true;
            }
            catch (Exception error) when (Recoverable(error))
            { note = Failure("Correction non réutilisée", error); return false; }
        }

        internal static bool Save(Document doc, FamilyInstance source, MaintenanceWorkbenchState state, out string note)
        {
            note = "";
            try
            {
                if (state == null || state.CommandPoint == null) throw new ArgumentException("Pointez d’abord le pivot ou le point de commande.");
                if (state.Axis < 0 || state.Axis > 6 || state.Axis == 6 && state.DetectedFrame == null)
                    throw new ArgumentException("Le repère de commande est incomplet.");
                // The explicit Save button authorises recording hypotheses; it
                // does NOT imply the 'verified' checkbox or manufacturer proof.
                source = Resolve(doc, source);
                var profile = MaintenanceAutoconfiguration.ReadSavedProfile(source, state);
                profile.Validate(); ValidateFrame(profile.Frame);
                Identity identity = Capture(doc, source);
                XYZ command = identity.Source.Inverse.OfPoint(state.CommandPoint);
                if (command.GetLength() > 1000000 / 304.8) throw new ArgumentException("Le point de commande est trop éloigné de l’équipement.");
                var record = new MaintenanceCorrectionRecord {
                    Signature = identity.Signature, Geometry = identity.Geometry, Parameters = identity.Parameters,
                    Connectors = identity.Connectors, Type = identity.Type, FamilyHint = identity.Family,
                    Operation = state.Operation.ToString(), SavedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Command = Values(command), X = Values(identity.Source.Inverse.OfVector(profile.Frame.BasisX)),
                    Y = Values(identity.Source.Inverse.OfVector(profile.Frame.BasisY)), Z = Values(identity.Source.Inverse.OfVector(profile.Frame.BasisZ)),
                    Measured = state.Measured, Manufacturer = state.Manufacturer && !string.IsNullOrWhiteSpace(state.Notice),
                    RotationAssumed = state.RotationAssumed, Notice = state.Notice ?? "", Warnings = state.FeatureWarnings
                        .Where(w => w != ReuseWarning && w != AssumedCourseWarning).Distinct(StringComparer.Ordinal).ToList(),
                    Fields = new Dictionary<string, double> {
                        {"Radius",profile.HandleRadius},{"Thickness",profile.HandleThickness},{"Angle",profile.RotationDegrees},{"InitialAngle",profile.InitialAngleDegrees},
                        {"ChainDrop",profile.ChainDrop},{"ChainSpacing",profile.ChainSpacing},{"Pull",profile.ChainPullStroke},{"Grip",profile.GripClearance},{"Margin",profile.Margin},
                        {"RX",profile.RemovalSize.X},{"RY",profile.RemovalSize.Y},{"RZ",profile.RemovalSize.Z},
                        {"Travel",profile.RemovalTravel},{"OffsetX",profile.RemovalCenterOffset.X},{"OffsetY",profile.RemovalCenterOffset.Y},{"OffsetZ",profile.RemovalCenterOffset.Z} }
                };
                if (!MaintenanceCorrectionData.Valid(record)) throw new ArgumentException("Les repères, cotes ou informations du geste sont invalides ou trop volumineux.");
                string path = MaintenanceCorrectionData.FilePath();
                MaintenanceCorrectionData.WithLock(path, () => {
                    var data = MaintenanceCorrectionData.Read(path);
                    data.Corrections.RemoveAll(r => r.Signature == record.Signature && r.Operation == record.Operation);
                    if (data.Corrections.Count >= MaintenanceCorrectionData.MaximumRecords) throw new InvalidDataException("La mémoire contient déjà 256 corrections.");
                    data.Corrections.Add(record); MaintenanceCorrectionData.Write(path, data); return true;
                });
                note = "Repères et réglages du geste mémorisés pour ce type et cette géométrie. Les hypothèses sont conservées ; aucun poste ni résultat de collision n’est enregistré.";
                return true;
            }
            catch (Exception error) when (Recoverable(error))
            { note = Failure("Correction non enregistrée", error); return false; }
        }

        internal static bool Remove(Document doc, FamilyInstance source, MaintenanceAccessOperation operation, out string note)
        {
            note = "";
            try
            {
                Identity identity = Capture(doc, source);
                string path = MaintenanceCorrectionData.FilePath();
                bool removed = MaintenanceCorrectionData.WithLock(path, () => {
                    var data = MaintenanceCorrectionData.Read(path);
                    if (data.Corrections.RemoveAll(r => r.Signature == identity.Signature && r.Operation == operation.ToString()) == 0) return false;
                    MaintenanceCorrectionData.Write(path, data); return true;
                });
                note = removed ? "Correction oubliée pour ce mécanisme et cette géométrie." : "Aucune correction de ce mécanisme à oublier pour la géométrie actuelle.";
                return removed;
            }
            catch (Exception error) when (Recoverable(error))
            { note = Failure("Correction non supprimée", error); return false; }
        }

        private static void ClearResults(MaintenanceWorkbenchState state)
        {
            state.Confirmed = false; state.FootPoint = null; state.SupportVerified = false; state.SupportNote = "";state.NoOperatorReason="";
            state.OperatorPosture = MaintenanceOperatorPosture.Standing; state.BodyYawDegrees = 0;
            state.RejectedPosture = MaintenanceOperatorPosture.Standing; state.RejectedBodyYawDegrees = 0;
            state.RejectedFoot = null; state.PlacementWarnings.Clear(); state.PostureWarnings.Clear(); state.PlacementDiagnostics.Clear(); state.BlockingObstacles.Clear();
            state.WholeGestureChecked = state.WholeGestureClear = state.SearchLimited = false;
            state.GestureUnknown.Clear(); state.GestureObstacles.Clear(); state.FixedObstacles.Clear(); state.Candidates.Clear();
        }

        private static FamilyInstance Resolve(Document doc, FamilyInstance source)
        {
            if (doc == null || source == null) throw new ArgumentException("Équipement absent du document actif.");
            source = doc.GetElement(source.UniqueId) as FamilyInstance;
            if (source == null) throw new ArgumentException("Équipement introuvable dans le document actif.");
            return source;
        }
        private static bool IsAngle(string key) => key == "Angle" || key == "InitialAngle";
        private static XYZ Point(double[] p) => new XYZ(p[0], p[1], p[2]);
        private static double[] Values(XYZ p) => new[] { p.X, p.Y, p.Z };
        private static bool Recoverable(Exception error) => !(error is OutOfMemoryException) && !(error is StackOverflowException) && !(error is AccessViolationException);
        private static string Failure(string prefix, Exception error)
            => prefix + " : " + (error.Message ?? error.GetType().Name).Substring(0, Math.Min(220, (error.Message ?? error.GetType().Name).Length));
        private static string Name(string value) => (value ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        private static void ValidateFrame(Transform frame)
        {
            if (frame == null || new[] { frame.Origin, frame.BasisX, frame.BasisY, frame.BasisZ }.Any(p => p == null || Values(p).Any(v => !MaintenanceCorrectionData.Finite(v))))
                throw new ArgumentException("Le repère doit être fini.");
            XYZ x = frame.BasisX, y = frame.BasisY, z = frame.BasisZ;
            if (Math.Abs(x.GetLength() - 1) > 1e-6 || Math.Abs(y.GetLength() - 1) > 1e-6 || Math.Abs(z.GetLength() - 1) > 1e-6
                || Math.Abs(x.DotProduct(y)) > 1e-6 || Math.Abs(x.DotProduct(z)) > 1e-6 || Math.Abs(y.DotProduct(z)) > 1e-6)
                throw new ArgumentException("Le repère doit être orthonormé sans changement d’échelle.");
        }

        private static Identity Capture(Document doc, FamilyInstance source)
        {
            source = Resolve(doc, source);
            var sourceFrame = SourceFrame(source); ValidateFrame(sourceFrame);
            var geometry = new Fingerprint(sourceFrame.Inverse);
            geometry.Element(doc, source);
            var parameters = new List<string>();
            ReadParameters(source, "instance", parameters); ReadParameters(source.Symbol, "type", parameters);
            string type = Name(source.Symbol.Name);
            if (type.Length == 0 || type.Length > 500) throw new InvalidDataException("Le type de l’équipement est indéterminé.");
            string family = source.Symbol.Family.Name ?? "";
            if (family.Length > 500) family = family.Substring(0, 500);
            var connectors = new List<string>();
            var manager = source.MEPModel?.ConnectorManager;
            if (manager != null)
                foreach (Connector connector in manager.Connectors)
                {
                    if (connector.ConnectorType == ConnectorType.Logical) continue;
                    Transform coordinate = connector.CoordinateSystem;
                    string dimensions = connector.Shape == ConnectorProfileType.Round ? geometry.Number(connector.Radius)
                        : geometry.Number(connector.Width) + "," + geometry.Number(connector.Height);
                    connectors.Add(connector.Domain + ":" + connector.Shape + ":" + geometry.Position(sourceFrame.Inverse.OfPoint(connector.Origin))
                        + ":" + geometry.Direction(sourceFrame.Inverse.OfVector(coordinate.BasisZ)) + ":" + dimensions);
                    if (connectors.Count > 128) throw new InvalidDataException("Trop de raccordements pour une correspondance fiable.");
                }
            var identity = new Identity { Source = sourceFrame, Type = type, Family = family,
                Geometry = geometry.Complete(), Parameters = MaintenanceCorrectionData.Digest(string.Join("\n", parameters.OrderBy(v => v, StringComparer.Ordinal))),
                Connectors = MaintenanceCorrectionData.Digest(string.Join("\n", connectors.OrderBy(v => v, StringComparer.Ordinal))) };
            identity.Signature = MaintenanceCorrectionData.Signature(identity.Geometry, identity.Parameters, identity.Connectors, identity.Type);
            return identity;
        }

        private static Transform SourceFrame(FamilyInstance source)
        {
            var native = source.GetTransform(); ValidateFrame(native);
            double determinant = native.BasisX.CrossProduct(native.BasisY).DotProduct(native.BasisZ);
            // Revit can bake a flip into the symbol geometry while exposing a
            // proper GetTransform (verified natively on a mirrored valve).
            // Reconstruct the PHYSICAL mapping before storing local anchors.
            // An already improper transform contains its reflection: do not
            // apply it twice. Ambiguous flip metadata is never guessed.
            if (!source.Mirrored || determinant < 0) return native;
            if (source.HandFlipped == source.FacingFlipped)
                throw new InvalidDataException("État miroir ambigu : le repère physique doit être vérifié avant de réutiliser une correction.");
            var frame = Transform.Identity; frame.Origin = native.Origin;
            frame.BasisX = source.HandFlipped ? -native.BasisX : native.BasisX;
            frame.BasisY = source.FacingFlipped ? -native.BasisY : native.BasisY;
            frame.BasisZ = native.BasisZ;
            return frame;
        }

        private static void ReadParameters(Element element, string scope, List<string> result)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Definition == null) continue;
                string name = Name(parameter.Definition.Name), value = null;
                // Project position, levels and host offsets are not family
                // dimensions. Their resulting geometry is checked separately.
                long id = BoosterIds.Value(parameter.Id);
                string builtin = id < 0 ? Enum.GetName(typeof(BuiltInParameter), (int)id) ?? "" : "";
                if (builtin.Contains("ELEVATION") || builtin.Contains("_LEVEL") || builtin.Contains("_OFFSET")) continue;
                if (parameter.StorageType == StorageType.Double)
                {
                    string spec = parameter.Definition.GetDataType()?.TypeId ?? "";
                    if (!(spec.Contains(":length-") || spec.Contains(":angle-") || spec.Contains(":number-") || spec.Contains(":area-") || spec.Contains(":volume-"))) continue;
                    double number = parameter.AsDouble();
                    if (!MaintenanceCorrectionData.Finite(number)) throw new InvalidDataException("Paramètre géométrique non fini : " + name);
                    // Fine parameter precision intentionally rejects a variant
                    // even when its visible change is below the geometry grid.
                    value = Math.Round(number, 8).ToString("R", CultureInfo.InvariantCulture);
                }
                else if (parameter.StorageType == StorageType.Integer) value = parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                else if (parameter.StorageType == StorageType.String && new[] { "modele", "modèle", "model", "fabricant", "manufacturer", "reference", "référence", "produit", "product", "article", "levier", "poignee", "poignée", "handle", "volant", "handwheel", "chaine", "chaîne", "chain", "actionneur", "actuator", "course", "travel" }.Any(name.Contains))
                    value = Name(parameter.AsString());
                if (value == null) continue;
                if (name.Length > 500 || value.Length > 2000 || result.Count >= 1024) throw new InvalidDataException("Paramètres trop nombreux ou volumineux pour une correspondance fiable.");
                result.Add(Newtonsoft.Json.JsonConvert.SerializeObject(new[] {scope, name, parameter.StorageType.ToString(), value}));
            }
        }

        // Analytical/topological descriptors, sorted independently of native
        // enumeration order. No triangle-count or triangle-order hash. The
        // 0.5mm coordinate grid is a matching tolerance, never a scale factor.
        private sealed class Fingerprint
        {
            private readonly Transform _local;
            private readonly List<string> _features = new List<string>();
            private readonly HashSet<string> _elements = new HashSet<string>();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private int _points, _objects, _physical;
            internal Fingerprint(Transform local) { _local = local; }
            private void Budget()
            {
                if (++_objects > 25000 || _features.Count > 20000 || _points > 150000 || _clock.ElapsedMilliseconds > 4000)
                    throw new InvalidDataException("Géométrie trop complexe pour une correspondance complète ; correction non réutilisée.");
            }
            internal string Number(double feet)
            {
                if (!MaintenanceCorrectionData.Finite(feet) || Math.Abs(feet) > 1000000) throw new InvalidDataException("Géométrie non finie ou hors limites.");
                // At a half-grid boundary (e.g. 30.75mm), world/local
                // cancellation several kilometres from the origin must not
                // choose a different bucket. Stabilise numerical noise at
                // 1e-6mm BEFORE the unchanged 0.5mm matching quantisation.
                double millimetres = Math.Round(feet * 304.8, 6, MidpointRounding.AwayFromZero);
                return IntegerText(Math.Round(millimetres * 2, MidpointRounding.AwayFromZero));
            }
            private static string IntegerText(double number) => number == 0 ? "0" : number.ToString("0", CultureInfo.InvariantCulture);
            internal string Position(XYZ p) { if (p == null) throw new InvalidDataException("Point absent."); return Number(p.X) + "," + Number(p.Y) + "," + Number(p.Z); }
            internal string Direction(XYZ p)
            {
                if (p == null || Values(p).Any(v => !MaintenanceCorrectionData.Finite(v)) || p.GetLength() < 1e-8) throw new InvalidDataException("Direction absente ou non finie.");
                p = p.Normalize();
                return IntegerText(Math.Round(p.X * 1000000)) + "," + IntegerText(Math.Round(p.Y * 1000000)) + "," + IntegerText(Math.Round(p.Z * 1000000));
            }
            internal void Element(Document doc, FamilyInstance element)
            {
                if (!_elements.Add(element.UniqueId)) return;
                if (_elements.Count > 128) throw new InvalidDataException("Trop de sous-composants pour une correspondance fiable.");
                using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false, ComputeReferences = false })
                using (var geometry = element.get_Geometry(options))
                {
                    if (geometry == null) throw new InvalidDataException("Géométrie de l’équipement indisponible.");
                    Read(geometry, Transform.Identity, 0);
                }
                foreach (ElementId id in element.GetSubComponentIds())
                    if (doc.GetElement(id) is FamilyInstance nested) Element(doc, nested);
            }
            private void Read(GeometryElement element, Transform transform, int depth)
            {
                if (element == null || depth > 24) throw new InvalidDataException("Géométrie imbriquée incomplète.");
                // Compose once while the point is still local. Converting each
                // small family point to a huge world coordinate and back loses
                // precision unnecessarily, especially at quantisation ties.
                Transform local = _local.Multiply(transform);
                foreach (GeometryObject item in element)
                {
                    Budget();
                    if (item == null) throw new InvalidDataException("Objet géométrique incomplet.");
                    if (item is GeometryInstance instance)
                    { using (var child = instance.GetSymbolGeometry()) Read(child, transform.Multiply(instance.Transform), depth + 1); }
                    else if (item is Solid solid)
                    {
                        if (solid.Faces.Size == 0) continue;
                        if (!MaintenanceCorrectionData.Finite(solid.Volume)) throw new InvalidDataException("Volume géométrique non fini.");
                        _physical++;
                        _features.Add("solid:" + Math.Round(solid.Volume * 28316846.592, 1).ToString("R", CultureInfo.InvariantCulture));
                        foreach (Face face in solid.Faces)
                        {
                            Budget();
                            if (face == null || !MaintenanceCorrectionData.Finite(face.Area)) throw new InvalidDataException("Face incomplète.");
                            var boundaries = new List<string>();
                            foreach (EdgeArray loop in face.EdgeLoops)
                            {
                                var edges = new List<string>();
                                foreach (Edge edge in loop) using (var curve = edge.AsCurve()) edges.Add(Curve(curve, local));
                                boundaries.Add(string.Join(";", edges.OrderBy(s => s, StringComparer.Ordinal)));
                            }
                            if (boundaries.Count == 0) throw new InvalidDataException("Contour de face indisponible.");
                            string surface = face.GetType().Name;
                            if (face is CylindricalFace cylinder)
                                surface += ":" + Number(cylinder.get_Radius(0).GetLength()) + ":" + Direction(local.OfVector(cylinder.Axis));
                            // Sample the underlying surface as well as its edges:
                            // equal boundaries alone do not prove equal bulges.
                            var uv = face.GetBoundingBox();
                            var samples = new List<string>();
                            for (int u = 0; u < 3; u++) for (int v = 0; v < 3; v++)
                            {
                                var point = face.Evaluate(new UV(uv.Min.U + (uv.Max.U - uv.Min.U) * u / 2, uv.Min.V + (uv.Max.V - uv.Min.V) * v / 2));
                                samples.Add(Position(local.OfPoint(point))); _points++;
                            }
                            _features.Add(surface + ":" + Math.Round(face.Area * 92903.04, 1).ToString("R", CultureInfo.InvariantCulture)
                                + ":" + string.Join("|", boundaries.OrderBy(s => s, StringComparer.Ordinal)) + ":" + string.Join(";", samples.OrderBy(s => s, StringComparer.Ordinal)));
                        }
                    }
                    else if (item is Mesh mesh)
                    {
                        _physical++;
                        var patches = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                        for (int i = 0; i < mesh.NumTriangles; i++)
                        {
                            Budget(); var triangle = mesh.get_Triangle(i);
                            if (triangle == null) throw new InvalidDataException("Triangle de maillage absent.");
                            XYZ[] points = Enumerable.Range(0, 3).Select(j => local.OfPoint(triangle.get_Vertex(j))).ToArray();
                            _points += 3;
                            XYZ normal = (points[1] - points[0]).CrossProduct(points[2] - points[0]);
                            if (normal.GetLength() < 1e-12) continue;
                            normal = normal.Normalize();
                            // Give the same plane the same sign irrespective of
                            // native triangle winding and mirrored instances.
                            if (Math.Abs(normal.X) > 1e-6 ? normal.X < 0 : Math.Abs(normal.Y) > 1e-6 ? normal.Y < 0 : normal.Z < 0) normal = -normal;
                            string plane = Direction(normal) + ":" + Number(normal.DotProduct(points[0]));
                            if (!patches.TryGetValue(plane, out var edges)) patches[plane] = edges = new Dictionary<string, int>(StringComparer.Ordinal);
                            for (int j = 0; j < 3; j++)
                            {
                                string a = Position(points[j]), b = Position(points[(j + 1) % 3]);
                                string edge = string.CompareOrdinal(a, b) < 0 ? a + ";" + b : b + ";" + a;
                                edges.TryGetValue(edge, out int count); edges[edge] = count + 1;
                                if (count > 1) throw new InvalidDataException("Maillage non manifold : correspondance incertaine.");
                            }
                        }
                        if (patches.Count == 0) throw new InvalidDataException("Maillage incomplet.");
                        // Remove internal coplanar edges. Alternate diagonals of
                        // a planar face do not invalidate a correction. Curved
                        // tessellation changes conservatively refuse a match.
                        foreach (var patch in patches)
                            _features.Add("mesh-patch:" + patch.Key + ":" + string.Join("|", patch.Value.Where(kv => kv.Value == 1).Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal)));
                    }
                    else if (item is Curve curve) _features.Add("curve:" + Curve(curve, local));
                    else if (!(item is Autodesk.Revit.DB.Point))
                        throw new InvalidDataException("Type géométrique non pris en charge : " + item.GetType().Name);
                }
            }
            private string Curve(Curve curve, Transform local)
            {
                Budget();
                if (curve == null) throw new InvalidDataException("Courbe absente.");
                var points = curve.Tessellate();
                if (points == null || points.Count == 0) throw new InvalidDataException("Courbe illisible.");
                _points += points.Count;
                Budget();
                var samples = points.Select(p => Position(local.OfPoint(p))).Distinct().OrderBy(s => s, StringComparer.Ordinal);
                return curve.GetType().Name + ":" + Number(curve.Length) + ":" + string.Join(";", samples);
            }
            internal string Complete()
            {
                Budget();
                if (_physical == 0 || _features.Count == 0) throw new InvalidDataException("Aucune géométrie physique complète pour mémoriser ce mécanisme.");
                return MaintenanceCorrectionData.Digest(string.Join("\n", _features.OrderBy(s => s, StringComparer.Ordinal)));
            }
        }
    }
}

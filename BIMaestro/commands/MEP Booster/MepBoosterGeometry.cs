using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMaestro.MepBooster
{
    internal static class BoosterIds
    {
        internal static long Value(ElementId id)
        {
            if (id == null) return long.MinValue;
#if REVIT2024
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
    }

    internal sealed class BoosterPart
    {
        public ElementId Id;
        public XYZ Center, Axis, FlipAxis;
        public XYZ FlipCenter;
        internal BoosterKinematics Motion;
        internal bool Hosted, GeometryLoaded;
        private int _remainingPoints;
        public double Length;
        public bool CanFlip;
        public readonly List<XYZ[]> Edges = new List<XYZ[]>();
        internal static bool SupportsCategory(Element element)
        {
            long category = BoosterIds.Value(element?.Category?.Id);
            return category == (long)BuiltInCategory.OST_PipeAccessory || category == (long)BuiltInCategory.OST_PipeFitting;
        }

        public Transform Rotation(double degrees, bool flip) =>
            Transform.CreateRotationAtPoint(flip ? FlipAxis : Axis, degrees * Math.PI / 180, flip ? FlipCenter : Center);
        internal bool CanApply(double degrees, bool flip) => (!flip || !Hosted)
            && (Motion == null ? (!flip || CanFlip) : Motion.Ports.All(p => !p.Connected || p.Shape == (int)ConnectorProfileType.Round)
                && Motion.ConnectionMap(degrees, flip) != null);

        public static BoosterPart Read(FamilyInstance instance, bool geometry)
        {
            if (instance == null || !SupportsCategory(instance))
                throw new InvalidOperationException("Sélectionnez des accessoires ou des raccords de canalisation.");
            if (instance.Pinned || instance.GroupId != ElementId.InvalidElementId || instance.SuperComponent != null)
                throw new InvalidOperationException("Un accessoire est verrouillé, imbriqué ou appartient à un groupe.");
            var ports = Ports(instance).OrderBy(c => c.Id).ToList();
            var motion = BoosterKinematics.Create(ports.Select(c =>
            {
                XYZ p = c.Origin, d = c.CoordinateSystem.BasisZ;
                bool round = c.Shape == ConnectorProfileType.Round;
                return new BoosterPortPose { Key = BoosterIds.Value(instance.Id) + ":" + c.Id,
                    Position = new System.Windows.Media.Media3D.Point3D(p.X, p.Y, p.Z),
                    Direction = new System.Windows.Media.Media3D.Vector3D(d.X, d.Y, d.Z),
                    Connected = c.IsConnected, Shape = (int)c.Shape,
                    SizeA = round ? c.Radius : c.Width, SizeB = round ? c.Radius : c.Height };
            }).ToArray());
            var part = new BoosterPart
            {
                Id = instance.Id, Motion = motion, Hosted = instance.Host != null,
                Center = new XYZ(motion.Center.X, motion.Center.Y, motion.Center.Z),
                FlipCenter = new XYZ(motion.FlipCenter.X, motion.FlipCenter.Y, motion.FlipCenter.Z),
                Axis = new XYZ(motion.Axis.X, motion.Axis.Y, motion.Axis.Z),
                FlipAxis = new XYZ(motion.FlipAxis.X, motion.FlipAxis.Y, motion.FlipAxis.Z),
                Length = ports.Max(p => ports.Max(q => p.Origin.DistanceTo(q.Origin)))
            };
            part.CanFlip = part.CanApply(180, true);
            if (geometry) part.LoadEdges(instance, 900);
            return part;
        }

        internal void LoadEdges(FamilyInstance instance, int pointBudget)
        {
            if (GeometryLoaded) return;
            GeometryLoaded = true; _remainingPoints = pointBudget;
            try
            {
                using (var options = new Options { DetailLevel = ViewDetailLevel.Coarse })
                {
                    var shape = instance.get_Geometry(options);
                    if (shape != null) ReadEdges(shape);
                }
            }
            catch { Edges.Clear(); } // Axes/arrows remain available if a family's geometry is unavailable.
        }

        internal static IEnumerable<Connector> Ports(FamilyInstance instance) =>
            instance.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping && c.ConnectorType == ConnectorType.End)
            ?? Enumerable.Empty<Connector>();

        private void ReadEdges(GeometryElement geometry)
        {
            foreach (GeometryObject item in geometry)
            {
                if (Edges.Count >= 80 || _remainingPoints < 2) return;
                if (item is GeometryInstance nested)
                {
                    using (var expanded = nested.GetInstanceGeometry()) ReadEdges(expanded);
                }
                else if (item is Solid solid)
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        var points = edge.Tessellate();
                        int count = Math.Min(points.Count, Math.Min(32, _remainingPoints));
                        if (count >= 2)
                        {
                            Edges.Add(Enumerable.Range(0, count).Select(i => points[i * (points.Count - 1) / (count - 1)]).ToArray());
                            _remainingPoints -= count;
                        }
                        if (Edges.Count >= 80 || _remainingPoints < 2) break;
                    }
                }
            }
        }
    }

    internal static class BoosterOperations
    {
        private sealed class PortState
        {
            public ElementId Owner;
            public int PortId;
            public XYZ Origin;
            public XYZ BasisX, BasisZ;
            public string[] Links;
            public string Key => BoosterIds.Value(Owner) + ":" + PortId;
        }

        private static string Key(Connector port) => BoosterIds.Value(port.Owner.Id) + ":" + port.Id;
        private static Connector Resolve(Document doc, PortState state)
        {
            Element owner = doc.GetElement(state.Owner);
            var manager = (owner as MEPCurve)?.ConnectorManager ?? (owner as FamilyInstance)?.MEPModel?.ConnectorManager;
            return manager?.Lookup(state.PortId) ?? throw new InvalidOperationException("Un connecteur n’est plus disponible : opération annulée.");
        }

        private static string[] Links(Connector port) => port.AllRefs.Cast<Connector>()
            .Where(c => c.Owner.Id != port.Owner.Id && c.ConnectorType == ConnectorType.End)
            .Select(Key).OrderBy(x => x).ToArray();

        internal static void Apply(Document doc, IList<BoosterPart> parts, double angle, bool flip)
        {
            // Re-read capabilities at execution time: a preview is never authority to mutate stale elements.
            var current = parts.Select(p => BoosterPart.Read(doc.GetElement(p.Id) as FamilyInstance, false)).ToList();
            if (current.Any(p => !p.CanApply(angle, flip)))
                throw new InvalidOperationException("Cette action déplacerait une extrémité raccordée. Libérez la branche concernée ou choisissez un autre angle.");
            var ports = current.SelectMany(p => BoosterPart.Ports((FamilyInstance)doc.GetElement(p.Id))).ToList();
            var selectedKeys = new HashSet<string>(ports.Select(Key));
            var mappedPorts = new Dictionary<string, string>();
            foreach (var part in current)
            {
                foreach (var mapping in part.Motion.ConnectionMap(angle, flip)) mappedPorts.Add(mapping.Key, mapping.Value);
            }
            string Map(string key) => mappedPorts.TryGetValue(key, out var value) ? value : key;
            // Include adjacent owners so an automatic movement of a neighbouring pipe is detected.
            var neighbours = ports.SelectMany(p => p.AllRefs.Cast<Connector>())
                .Where(c => c.ConnectorType == ConnectorType.End && c.Owner.Id != ElementId.InvalidElementId)
                .Select(c => c.Owner).GroupBy(e => BoosterIds.Value(e.Id)).Select(g => g.First()).ToList();
            foreach (Element neighbour in neighbours)
            {
                var manager = (neighbour as MEPCurve)?.ConnectorManager
                    ?? (neighbour as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (manager != null) ports.AddRange(manager.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End));
            }
            var before = ports.GroupBy(Key).Select(g => g.First())
                .Select(c => new PortState { Owner = c.Owner.Id, PortId = c.Id, Origin = c.Origin,
                    BasisX = c.CoordinateSystem.BasisX, BasisZ = c.CoordinateSystem.BasisZ, Links = Links(c) })
                .ToDictionary(p => p.Key);
            var edges = before.Values.Where(p => selectedKeys.Contains(p.Key))
                .SelectMany(p => p.Links.Select(link => string.CompareOrdinal(p.Key, link) < 0
                    ? Tuple.Create(p.Key, link) : Tuple.Create(link, p.Key)))
                .Distinct().ToList();
            // Expected graph is indexed by ACTUAL post-rotation ports, including newly occupied
            // ports which were previously free (e.g. a tee's exchanged run ends).
            var expected = before.Values.ToDictionary(p => p.Key,
                p => selectedKeys.Contains(p.Key) ? new HashSet<string>() : new HashSet<string>(p.Links.Select(Map)));
            foreach (var edge in edges)
            {
                expected[Map(edge.Item1)].Add(Map(edge.Item2));
                expected[Map(edge.Item2)].Add(Map(edge.Item1));
            }
            using (var transaction = new Transaction(doc, "MEP Booster — " + (flip ? "inversion" : angle + "°")))
            {
                transaction.Start();
                var failures = new RejectFailures();
                transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions()
                    .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                try
                {
                    foreach (var edge in edges)
                        Resolve(doc, before[edge.Item1]).DisconnectFrom(Resolve(doc, before[edge.Item2]));
                    if (edges.Count > 0) doc.Regenerate();
                    foreach (var part in current)
                        ElementTransformUtils.RotateElement(doc, part.Id,
                            Line.CreateUnbound(flip ? part.FlipCenter : part.Center, flip ? part.FlipAxis : part.Axis), angle * Math.PI / 180);
                    doc.Regenerate();
                    if (edges.Count > 0)
                    {
                        // Ensure Revit did not move neighbours while rotating disconnected parts.
                        foreach (var state in before.Values)
                            if ((!selectedKeys.Contains(state.Key) || state.Links.Length > 0)
                                && state.Origin.DistanceTo(Resolve(doc, before[Map(state.Key)]).Origin) > 1e-5)
                                throw new InvalidOperationException("L’inversion déplacerait un point de raccordement.");
                        foreach (var edge in edges)
                        {
                            var a = Resolve(doc, before[Map(edge.Item1)]);
                            var b = Resolve(doc, before[Map(edge.Item2)]);
                            if (a.Origin.DistanceTo(b.Origin) > 1e-5 || a.Shape != ConnectorProfileType.Round
                                || b.Shape != ConnectorProfileType.Round || Math.Abs(a.Radius - b.Radius) > 1e-6
                                || a.CoordinateSystem.BasisZ.Normalize().DotProduct(b.CoordinateSystem.BasisZ.Normalize()) > -0.9999)
                                throw new InvalidOperationException("Les extrémités ne sont pas compatibles après inversion.");
                            if (!a.IsConnectedTo(b)) a.ConnectTo(b);
                        }
                        doc.Regenerate();
                    }
                    foreach (var state in before.Values)
                    {
                        var actual = Resolve(doc, state);
                        var slot = Resolve(doc, before[Map(state.Key)]);
                        if (((!selectedKeys.Contains(state.Key) || state.Links.Length > 0) && state.Origin.DistanceTo(slot.Origin) > 1e-5)
                            || (!selectedKeys.Contains(state.Key) && (!state.BasisX.IsAlmostEqualTo(actual.CoordinateSystem.BasisX)
                                || !state.BasisZ.IsAlmostEqualTo(actual.CoordinateSystem.BasisZ)))
                            || !expected[state.Key].SetEquals(Links(actual)))
                            throw new InvalidOperationException("Cette rotation modifierait les raccordements ou déplacerait le réseau.");
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException(failures.Message ?? "Revit a refusé la rotation.");
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    throw;
                }
            }
        }

        private sealed class RejectFailures : IFailuresPreprocessor
        {
            public string Message;
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var failures = accessor.GetFailureMessages();
                if (failures.Count == 0) return FailureProcessingResult.Continue;
                Message = "Opération annulée : " + string.Join("\n", failures.Select(f => f.GetDescriptionText()));
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }
    }
}

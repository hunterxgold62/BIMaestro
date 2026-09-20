using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Analyse
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryNetwork
    {
        public string Kind { get; set; }
        public string SystemType { get; set; }
        public double[] Start { get; set; }
        public double[] End { get; set; }
        public double Diameter { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double[] SectionX { get; set; }
        public bool Placeholder { get; set; }
        public List<double[]> Points { get; set; }
        public double[] StartTangent { get; set; }
        public double[] EndTangent { get; set; }
    }
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    internal sealed class HistoryPort
    {
        public double[] Point { get; set; }
        public double[] Direction { get; set; }
        public int Domain { get; set; }
        public int Shape { get; set; }
        public double? Diameter { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public int? Id { get; set; }
        public double? Angle { get; set; }
    }
    internal sealed class HistoryConnection
    {
        public HistoryPort Port { get; set; }
        public string Peer { get; set; }
        public HistoryPort PeerPort { get; set; }
    }
    internal static class ElementHistoryNetwork
    {

        internal static XYZ Vector(double[] p) => new XYZ(p[0], p[1], p[2]);
        private static double[] Pack(XYZ p) => new[] { p.X, p.Y, p.Z };
        internal static List<Connector> Ports(Element element)
        {
            var manager = (element as MEPCurve)?.ConnectorManager ?? (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            return manager == null ? new List<Connector>() : manager.Connectors.Cast<Connector>()
                .Where(c => c.ConnectorType == ConnectorType.End || c.ConnectorType == ConnectorType.Curve
                    || c.ConnectorType == ConnectorType.Physical).ToList();
        }
        private static HistoryPort CapturePort(Connector c) => new HistoryPort
        {
            Point = Pack(c.Origin), Direction = Pack(c.CoordinateSystem.BasisZ), Domain = (int)c.Domain, Shape = (int)c.Shape,
            Id = c.Id,
            Diameter = c.Shape == ConnectorProfileType.Round ? (double?)(c.Radius * 2) : null,
            Width = c.Shape != ConnectorProfileType.Round ? (double?)c.Width : null,
            Height = c.Shape != ConnectorProfileType.Round ? (double?)c.Height : null
        };
        internal static List<HistoryPort> CaptureFamilyPorts(FamilyInstance instance)
        {
            return Ports(instance).Where(c => c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac
                || c.Domain == Domain.DomainCableTrayConduit).Select(c =>
            {
                var saved = CapturePort(c);
                if (c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac || c.Domain == Domain.DomainCableTrayConduit)
                    saved.Angle = c.Angle;
                return saved;
            }).ToList();
        }

        internal static void RestoreFamilyPorts(Document doc, FamilyInstance instance, HistoryRecipe recipe)
        {
            var saved = recipe.Ports ?? (recipe.Connections ?? new List<HistoryConnection>()).Select(c => c.Port)
                .GroupBy(p => Newtonsoft.Json.JsonConvert.SerializeObject(p.Point)).Select(g => g.First()).ToList();
            if (saved.Count == 0) return;
            doc.Regenerate();
            if (saved.All(p => Match(instance, p) != null)) return;
            var current = Ports(instance);
            // Connector IDs are stable within the retained family definition. Old
            // histories have no IDs: test each mapping, commit only an exact geometry match.
            var mappings = new List<List<int>>();
            BuildPortMappings(saved, current, 0, new List<int>(), mappings);
            string lastFailure = null;
            foreach (var mapping in mappings)
            foreach (var legacyAngleScale in recipe.Ports == null && saved.Count == 2 ? new[] { 0.0, 1.0, 0.5 } : new[] { 0.0 })
            {
                using (var attempt = new SubTransaction(doc))
                {
                    attempt.Start();
                    try
                    {
                        // Fresh, disconnected fittings expose parameters which Revit
                        // marked read-only while the original fitting was connected.
                        for (int pass = 0; pass < 2; pass++)
                        {
                            for (int i = 0; i < saved.Count; i++)
                            {
                                var port = instance.MEPModel.ConnectorManager.Lookup(mapping[i]);
                                var target = saved[i];
                                if (target.Diameter.HasValue && Math.Abs(port.Radius * 2 - target.Diameter.Value) > 1e-8)
                                {
                                    if (!SetAssociated(instance, port, BuiltInParameter.CONNECTOR_DIAMETER, target.Diameter.Value)
                                        && !SetAssociated(instance, port, BuiltInParameter.CONNECTOR_RADIUS, target.Diameter.Value / 2))
                                        port.Radius = target.Diameter.Value / 2;
                                }
                                if (target.Width.HasValue && Math.Abs(port.Width - target.Width.Value) > 1e-8) port.Width = target.Width.Value;
                                if (target.Height.HasValue && Math.Abs(port.Height - target.Height.Value) > 1e-8) port.Height = target.Height.Value;
                                double? angle = target.Angle;
                                // Legacy elbows recorded the two end normals but not
                                // the angle parameter. Try the deflection (or half-angle
                                // connector convention); only exact endpoint validation
                                // below can authorize committing such a reconstruction.
                                if (!angle.HasValue && legacyAngleScale > 0)
                                    angle = legacyAngleScale * (Math.PI - Vector(saved[0].Direction).AngleTo(Vector(saved[1].Direction)));
                                if (angle.HasValue && Math.Abs(port.Angle - angle.Value) > 1e-8) port.Angle = angle.Value;
                                doc.Regenerate();
                            }
                        }
                        if (saved.All(p => Match(instance, p) != null)) { attempt.Commit(); return; }
                    }
                    catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                    catch (Exception ex) { lastFailure = ex.Message; }
                    finally { if (attempt.GetStatus() == TransactionStatus.Started) attempt.RollBack(); }
                }
            }
            throw new InvalidOperationException("La géométrie des connecteurs du raccord ne correspond pas à l’historique. "
                + DescribeMismatch(instance, saved.First(p => Match(instance, p) == null))
                + (lastFailure == null ? "" : " Paramétrage : " + lastFailure));
        }

        private static void BuildPortMappings(List<HistoryPort> saved, List<Connector> actual, int index,
            List<int> chosen, List<List<int>> result)
        {
            if (result.Count >= 24) return;
            if (index == saved.Count) { result.Add(chosen.ToList()); return; }
            var target = saved[index];
            foreach (var port in actual.Where(c => !chosen.Contains(c.Id) && (int)c.Domain == target.Domain
                && (int)c.Shape == target.Shape && (!target.Id.HasValue || target.Id.Value == c.Id)))
            {
                chosen.Add(port.Id); BuildPortMappings(saved, actual, index + 1, chosen, result); chosen.RemoveAt(chosen.Count - 1);
            }
        }

        private static bool SetAssociated(FamilyInstance instance, Connector connector, BuiltInParameter connectorParameter, double value)
        {
            using (var info = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo)
            {
                if (info == null) return false;
                var id = info.GetAssociateFamilyParameterId(new ElementId(connectorParameter));
                if (id == ElementId.InvalidElementId) return false;
                var parameter = instance.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id == id);
                if (parameter == null && instance.Document.GetElement(id) is ParameterElement definition)
                    parameter = instance.get_Parameter(definition.GetDefinition());
                return parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double && parameter.Set(value);
            }
        }

        // Some reducers expose only solver-controlled dimensions. Let Revit's fitting
        // factory size them against temporary, explicitly selected end connectors.
        // The stubs and wrong candidates never escape this subtransaction.
        internal static FamilyInstance RebuildWithSizingStubs(Document doc, FamilyInstance original, HistoryRecipe recipe, bool protectDependents = false)
        {
            try { return RebuildWithSizingStubsAttempt(doc, original, recipe, protectDependents, false); }
            catch (InvalidOperationException)
            {
                // A transition's primary end controls its local offset parameters.
                // Try the opposite creation order after the first candidate rolled
                // back. Only a full historical connector match may be committed.
                return RebuildWithSizingStubsAttempt(doc, original, recipe, protectDependents, true);
            }
        }

        private static FamilyInstance RebuildWithSizingStubsAttempt(Document doc, FamilyInstance original, HistoryRecipe recipe, bool protectDependents, bool reverseEnds)
        {
            var saved = recipe.Ports ?? (recipe.Connections ?? new List<HistoryConnection>()).Select(c => c.Port)
                .GroupBy(p => Newtonsoft.Json.JsonConvert.SerializeObject(p.Point)).Select(g => g.First()).ToList();
            if (saved.Count != 2 || !(original.MEPModel is MechanicalFitting fitting))
                throw new InvalidOperationException("Ce raccord ne permet pas le redimensionnement natif à deux extrémités.");
            var part = fitting.PartType;
            if (reverseEnds) saved = saved.AsEnumerable().Reverse().ToList();
            bool takeoff = part == PartType.SpudAdjustable || part == PartType.SpudPerpendicular;
            if (part != PartType.Transition && part != PartType.Elbow && part != PartType.Union && !takeoff)
                throw new InvalidOperationException("Type de raccord non pris en charge par le redimensionnement natif.");
            using (var attempt = new SubTransaction(doc))
            {
                attempt.Start();
                try
                {
                    var ends = new List<Connector>();
                    var stubs = new List<ElementId>();
                    var temporaryTypes = new List<ElementId>();
                    var wantedType = doc.GetElement(recipe.Type).Id;
                    foreach (var target in saved)
                    {
                        var start = Vector(target.Point);
                        var end = start + Vector(target.Direction).Normalize() * Math.Max(3, (target.Diameter ?? 1) * 6);
                        MEPCurve stub;
                        if (target.Domain == (int)Domain.DomainPiping)
                        {
                            var type = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                            if (!takeoff) type = SizingType(doc, type, wantedType, part, temporaryTypes);
                            var system = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
                            stub = Pipe.Create(doc, system, type, doc.GetElement(recipe.Level).Id, start, end);
                            stub.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(target.Diameter.Value);
                        }
                        else if (target.Domain == (int)Domain.DomainHvac)
                        {
                            var type = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>()
                                .First(t => (int)t.Shape == target.Shape).Id;
                            if (!takeoff) type = SizingType(doc, type, wantedType, part, temporaryTypes);
                            var system = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
                            stub = Duct.Create(doc, system, type, doc.GetElement(recipe.Level).Id, start, end);
                            if (target.Diameter.HasValue) stub.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).Set(target.Diameter.Value);
                            else
                            {
                                stub.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(target.Width.Value);
                                stub.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).Set(target.Height.Value);
                            }
                        }
                        else throw new InvalidOperationException("Domaine non pris en charge pour le redimensionnement natif.");
                        stubs.Add(stub.Id);
                        doc.Regenerate();
                        ends.Add(Ports(stub).OrderBy(c => c.Origin.DistanceTo(start)).First());
                    }
                    FamilyInstance created;
                    if (takeoff)
                    {
                        var hostLink = (recipe.Connections ?? new List<HistoryConnection>()).FirstOrDefault(c =>
                            Vector(c.Port.Point).DistanceTo(Vector(c.PeerPort.Point)) > 1e-4 && FindHistoricalCurve(doc, c.Peer) != null);
                        if (hostLink == null) throw new InvalidOperationException("Canalisation porteuse du piquage absente ou non identifiée dans l’historique.");
                        var host = FindHistoricalCurve(doc, hostLink.Peer);
                        var previousType = host.GetTypeId();
                        var previousCurve = ((LocationCurve)host.Location).Curve;
                        var startBefore = previousCurve.GetEndPoint(0); var endBefore = previousCurve.GetEndPoint(1);
                        var branchIndex = saved.FindIndex(p => Vector(p.Point).DistanceTo(Vector(hostLink.Port.Point)) > 1e-4);
                        if (branchIndex < 0) throw new InvalidOperationException("Extrémité de branche du piquage introuvable.");
                        host.ChangeTypeId(SizingType(doc, previousType, wantedType, part, temporaryTypes));
                        doc.Regenerate();
                        created = doc.Create.NewTakeoffFitting(ends[branchIndex], host);
                        host.ChangeTypeId(previousType);
                        doc.Regenerate();
                        var curveAfter = ((LocationCurve)host.Location).Curve;
                        if (curveAfter.GetEndPoint(0).DistanceTo(startBefore)>1e-6 || curveAfter.GetEndPoint(1).DistanceTo(endBefore)>1e-6)
                            throw new InvalidOperationException("Le piquage déplacerait sa canalisation porteuse.");
                    }
                    else created = part == PartType.Transition ? doc.Create.NewTransitionFitting(ends[0], ends[1])
                        : part == PartType.Elbow ? doc.Create.NewElbowFitting(ends[0], ends[1])
                        : doc.Create.NewUnionFitting(ends[0], ends[1]);
                    if (created.GetTypeId() != wantedType)
                    {
                        var replacement = created.ChangeTypeId(wantedType);
                        if (replacement != ElementId.InvalidElementId) created = (FamilyInstance)doc.GetElement(replacement);
                    }
                    doc.Regenerate();
                    foreach (var port in Ports(created))
                        foreach (var peer in port.AllRefs.Cast<Connector>().Where(c => stubs.Contains(c.Owner.Id)).ToList())
                            if (port.IsConnectedTo(peer)) port.DisconnectFrom(peer);
                    doc.Delete(stubs);
                    doc.Delete(temporaryTypes);
                    doc.Regenerate();
                    var solvedPoint = (created.Location as LocationPoint)?.Point;
                    ElementHistoryReconstruction.ApplyParameters(doc, created, recipe.Parameters);
                    doc.Regenerate();
                    // The factory may reverse a reducer's local origin. Historical
                    // level offsets must not move its already solved world endpoints.
                    if (solvedPoint != null && created.Location is LocationPoint placed)
                    {
                        ElementTransformUtils.MoveElement(doc, created.Id, solvedPoint - placed.Point);
                        doc.Regenerate();
                    }
                    if (!takeoff && saved.Any(p => Match(created, p) == null)) AlignHistoricalPorts(doc, created, saved);
                    if (created.GetTypeId() != wantedType || saved.Any(p => Match(created, p) == null))
                        throw new InvalidOperationException("Le raccord natif ne reproduit pas exactement les connecteurs historiques. "
                            + string.Join(" / ", saved.Where(p => Match(created, p) == null).Select(p => DescribeMismatch(created, p)))
                            + " Connecteurs actuels : " + JsonConvert.SerializeObject(CaptureFamilyPorts(created)));
                    var originalId = original.Id;
                    var dependents = protectDependents ? original.GetDependentElements(null).ToDictionary(id => id, id => doc.GetElement(id)) : null;
                    // Revit owns anonymous centerline/internal nodes beneath a
                    // fitting. They are recreated with it, unlike tags/insulation or
                    // nested instances which must never be discarded by a repair.
                    var internalIds = new HashSet<ElementId>(dependents == null ? new List<ElementId>()
                        : dependents.Where(kv => kv.Value != null && kv.Value.GetType() == typeof(Element)
                            && string.IsNullOrEmpty(kv.Value.Name) && (kv.Value.Category == null
                                || kv.Value.Category.Id == new ElementId(BuiltInCategory.OST_PipeFittingCenterLine)
                                || kv.Value.Category.Id == new ElementId(BuiltInCategory.OST_DuctFittingCenterLine)))
                            .Select(kv => kv.Key).ToList());
                    var dependentDescriptions = dependents?.ToDictionary(kv => kv.Key,
                        kv => kv.Value?.GetType().Name + " : " + kv.Value?.Name + " (" + kv.Value?.Category?.Name + ")");
                    var removed = doc.Delete(originalId);
                    if (protectDependents && removed.Any(id => id != originalId && !internalIds.Contains(id)))
                        throw new InvalidOperationException("Le remplacement affecterait des éléments dépendants : raccord conservé sans modification. "
                            + string.Join(" / ", removed.Where(id => id != originalId && !internalIds.Contains(id))
                                .Select(id => dependentDescriptions.TryGetValue(id, out var description) ? description : id.ToString())));
                    attempt.Commit();
                    return created;
                }
                finally { if (attempt.GetStatus() == TransactionStatus.Started) attempt.RollBack(); }
            }
        }
        private static MEPCurve FindHistoricalCurve(Document doc, string uid)
        {
            return ElementHistoryReconstruction.FindOriginal(doc, uid) as MEPCurve
                ?? new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Cast<MEPCurve>()
                    .FirstOrDefault(c => ElementHistoryRestoration.GetOrigins(c)?.Contains(uid) == true);
        }

        private static void AlignHistoricalPorts(Document doc, FamilyInstance instance, List<HistoryPort> saved)
        {
            var ports = Ports(instance);
            if (ports.Any(c => c.IsConnected)) return;
            foreach (var anchor in ports.Where(c => SameSectionAndDirection(c, saved[0])))
            {
                var translation = Vector(saved[0].Point) - anchor.Origin;
                if (!saved.All(p => ports.Count(c => SameSectionAndDirection(c, p)
                    && (c.Origin + translation).DistanceTo(Vector(p.Point)) < 1e-4) == 1)) continue;
                // Some content places its insertion point 50 mm inside a connector.
                // Derive the translation from both ends, never a hardcoded offset.
                ElementTransformUtils.MoveElement(doc, instance.Id, translation);
                doc.Regenerate();
                return;
            }
        }

        private static bool SameSectionAndDirection(Connector c, HistoryPort saved) =>
            (int)c.Domain == saved.Domain && (int)c.Shape == saved.Shape
            && (!saved.Diameter.HasValue || Math.Abs(c.Radius * 2 - saved.Diameter.Value) < 1e-6)
            && (!saved.Width.HasValue || Math.Abs(c.Width - saved.Width.Value) < 1e-6)
            && (!saved.Height.HasValue || Math.Abs(c.Height - saved.Height.Value) < 1e-6)
            && c.CoordinateSystem.BasisZ.DistanceTo(Vector(saved.Direction)) < 1e-4;

        private static ElementId SizingType(Document doc, ElementId sourceType, ElementId fittingType, PartType part, List<ElementId> temporaryTypes)
        {
            var copy = ((ElementType)doc.GetElement(sourceType)).Duplicate("BIMaestro-sizing-" + Guid.NewGuid().ToString("N"));
            temporaryTypes.Add(copy.Id);
            var routing = (copy as PipeType)?.RoutingPreferenceManager ?? (copy as DuctType)?.RoutingPreferenceManager;
            var group = part == PartType.Transition ? RoutingPreferenceRuleGroupType.Transitions
                : part == PartType.Elbow ? RoutingPreferenceRuleGroupType.Elbows
                : part == PartType.Union ? RoutingPreferenceRuleGroupType.Unions : RoutingPreferenceRuleGroupType.Junctions;
            while (routing.GetNumberOfRules(group) > 0) routing.RemoveRule(group, 0);
            var rule = new RoutingPreferenceRule(fittingType, "Historical fitting");
            rule.AddCriterion(new PrimarySizeCriterion(0, double.MaxValue));
            routing.AddRule(group, rule);
            return copy.Id;
        }
        internal static List<HistoryConnection> CaptureConnections(Element element, List<string> warnings = null)
        {
            var links = new List<HistoryConnection>();
            List<Connector> ports;
            try { ports = Ports(element); }
            catch (Exception ex) { warnings?.Add("Connecteurs non enregistrés : " + ex.Message); return links; }
            foreach (var port in ports)
            {
                try
                {
                    foreach (Connector peer in port.AllRefs)
                    {
                        try
                        {
                            if (peer.Owner.Id != element.Id && !(peer.Owner is MEPSystem)
                                && (peer.ConnectorType == ConnectorType.End || peer.ConnectorType == ConnectorType.Curve
                                    || peer.ConnectorType == ConnectorType.Physical) && port.IsConnectedTo(peer))
                                links.Add(new HistoryConnection { Port = CapturePort(port), Peer = peer.Owner.UniqueId, PeerPort = CapturePort(peer) });
                        }
                        catch (Exception ex) { warnings?.Add("Connexion non enregistrée : " + ex.Message); }
                    }
                }
                catch (Exception ex) { warnings?.Add("Connecteur non enregistré : " + ex.Message); }
            }
            return links;
        }
        internal static HistoryRecipe Capture(Element element)
        {
            var curve = element as MEPCurve;
            string kind = element is Pipe ? "pipe" : element is Duct ? "duct" : element is Conduit ? "conduit" : element is CableTray ? "tray"
                : element is FlexPipe ? "flexPipe" : element is FlexDuct ? "flexDuct" : null;
            var flexPipe = element as FlexPipe; var flexDuct = element as FlexDuct;
            var points = flexPipe?.Points ?? flexDuct?.Points;
            var line = (curve.Location as LocationCurve)?.Curve as Line;
            if (kind == null || (points == null && line == null))
                throw new InvalidOperationException("Classe de réseau ou courbe non prise en charge : " + element.GetType().Name + ".");
            var start = points == null ? line.GetEndPoint(0) : points.First();
            var end = points == null ? line.GetEndPoint(1) : points.Last();
            var level = curve.ReferenceLevel ?? ElementHistoryReconstruction.FindLevel(element);
            var type = element.Document.GetElement(element.GetTypeId());
            if (level == null || type == null) throw new InvalidOperationException("Type ou niveau du réseau introuvable à la capture.");
            var systemId = element.get_Parameter(element is Pipe || element is FlexPipe ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId();
            var port = Ports(element).Where(c => c.ConnectorType == ConnectorType.End)
                .OrderBy(c => c.Origin.DistanceTo(start)).FirstOrDefault();
            // Non-round flexible sections need their full twist frame, not just tangents.
            if (points != null && port?.Shape != ConnectorProfileType.Round) throw new InvalidOperationException("Flexible non circulaire non pris en charge.");
            var warnings = new List<string>();
            return new HistoryRecipe
            {
                Kind = "network", Type = type.UniqueId, Level = level.UniqueId, LevelElevation = level.ProjectElevation,
                RestorationOrigins = ElementHistoryRestoration.GetOrigins(element), Connections = CaptureConnections(element, warnings), CaptureWarnings = warnings,
                // Placement/system built-ins are controlled by the geometry recipe; do
                // not replay stale offsets over the absolute start/end points.
                Parameters = ElementHistoryReconstruction.CaptureParameters(element)
                    .Where(p => p.BuiltIn == 0 || p.BuiltIn == (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).ToList(),
                Network = new HistoryNetwork
                {
                    Kind = kind, SystemType = systemId == null ? null : element.Document.GetElement(systemId)?.UniqueId,
                    Start = Pack(start), End = Pack(end), Points = points?.Select(Pack).ToList(),
                    StartTangent = points == null ? null : Pack(flexPipe?.StartTangent ?? flexDuct.StartTangent),
                    EndTangent = points == null ? null : Pack(flexPipe?.EndTangent ?? flexDuct.EndTangent),
                    Diameter = port?.Shape == ConnectorProfileType.Round ? port.Radius * 2 : 0,
                    Width = port != null && port.Shape != ConnectorProfileType.Round ? port.Width : 0,
                    Height = port != null && port.Shape != ConnectorProfileType.Round ? port.Height : 0,
                    SectionX = port == null ? null : Pack(port.CoordinateSystem.BasisX),
                    Placeholder = (element as Pipe)?.IsPlaceholder == true || (element as Duct)?.IsPlaceholder == true
                }
            };
        }
        internal static Element Create(Document doc, HistoryRecipe recipe)
        {
            var n = recipe.Network ?? throw new InvalidOperationException("Missing network geometry.");
            var type = doc.GetElement(recipe.Type).Id; var level = doc.GetElement(recipe.Level).Id;
            var start = Vector(n.Start); var end = Vector(n.End);
            var system = string.IsNullOrEmpty(n.SystemType) ? null : doc.GetElement(n.SystemType);
            MEPCurve result;
            if (n.Kind == "pipe" || n.Kind == "duct" || n.Kind == "flexPipe" || n.Kind == "flexDuct")
            {
                if (system == null) throw new InvalidOperationException("Le type de système du réseau est absent.");
                if (n.Kind == "flexPipe") result = FlexPipe.Create(doc, system.Id, type, level, Vector(n.StartTangent), Vector(n.EndTangent), n.Points.Select(Vector).ToList());
                else if (n.Kind == "flexDuct") result = FlexDuct.Create(doc, system.Id, type, level, Vector(n.StartTangent), Vector(n.EndTangent), n.Points.Select(Vector).ToList());
                else result = n.Kind == "pipe"
                    ? (MEPCurve)(n.Placeholder ? Pipe.CreatePlaceholder(doc, system.Id, type, level, start, end) : Pipe.Create(doc, system.Id, type, level, start, end))
                    : (n.Placeholder ? Duct.CreatePlaceholder(doc, system.Id, type, level, start, end) : Duct.Create(doc, system.Id, type, level, start, end));
            }
            else if (n.Kind == "conduit") result = Conduit.Create(doc, type, start, end, level);
            else if (n.Kind == "tray") result = CableTray.Create(doc, type, start, end, level);
            else throw new InvalidOperationException("Unsupported network element.");
            if (n.Diameter > 0) Set(result, n.Kind == "pipe" || n.Kind == "flexPipe" ? BuiltInParameter.RBS_PIPE_DIAMETER_PARAM
                : n.Kind == "conduit" ? BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM : BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, n.Diameter);
            if (n.Width > 0) Set(result, n.Kind == "tray" ? BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM : BuiltInParameter.RBS_CURVE_WIDTH_PARAM, n.Width);
            if (n.Height > 0) Set(result, n.Kind == "tray" ? BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM : BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, n.Height);
            doc.Regenerate();
            if (n.SectionX != null && n.Width > 0 && n.Points == null)
            {
                var port = Ports(result).OrderBy(c => c.Origin.DistanceTo(start)).First();
                var axis = (end - start).Normalize(); var from = port.CoordinateSystem.BasisX; var to = Vector(n.SectionX);
                var angle = Math.Atan2(axis.DotProduct(from.CrossProduct(to)), from.DotProduct(to));
                if (Math.Abs(angle) > 1e-8) ElementTransformUtils.RotateElement(doc, result.Id, Line.CreateBound(start, end), angle);
            }
            ElementHistoryReconstruction.ApplyParameters(doc, result, recipe.Parameters);
            return result;
        }
        private static void Set(Element element, BuiltInParameter id, double value)
        {
            var p = element.get_Parameter(id);
            if (p == null || p.IsReadOnly || !p.Set(value)) throw new InvalidOperationException("Network section could not be restored.");
        }
        // Align the full frame so vertical and sloped fittings do not return horizontal.
        internal static void Orient(Document doc, FamilyInstance instance, XYZ x, XYZ z)
        {
            Align(doc, instance, instance.GetTransform().BasisZ, z);
            var currentX = instance.GetTransform().BasisX;
            var angle = Math.Atan2(z.DotProduct(currentX.CrossProduct(x)), currentX.DotProduct(x));
            if (Math.Abs(angle) > 1e-8)
            {
                var point = ((LocationPoint)instance.Location).Point;
                ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(point, point + z), angle);
                doc.Regenerate();
            }
            var frame = instance.GetTransform();
            if (frame.BasisX.DistanceTo(x) > 1e-6 || frame.BasisZ.DistanceTo(z) > 1e-6)
                throw new InvalidOperationException("Family orientation could not be restored.");
        }
        private static void Align(Document doc, FamilyInstance instance, XYZ from, XYZ to)
        {
            var angle = from.AngleTo(to); if (angle < 1e-8) return;
            var axis = from.CrossProduct(to);
            if (axis.GetLength() < 1e-8) axis = from.CrossProduct(Math.Abs(from.DotProduct(XYZ.BasisZ)) < 0.9 ? XYZ.BasisZ : XYZ.BasisX);
            var point = ((LocationPoint)instance.Location).Point;
            ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(point, point + axis.Normalize()), angle);
            doc.Regenerate();
        }
        internal static Connector Match(Element element, HistoryPort saved)
        {
            if (element == null || saved == null) return null;
            var matches = Ports(element).Where(c => (int)c.Domain == saved.Domain && (int)c.Shape == saved.Shape
                && c.Origin.DistanceTo(Vector(saved.Point)) < 1e-4
                && (!saved.Diameter.HasValue || Math.Abs(c.Radius * 2 - saved.Diameter.Value) < 1e-6)
                && (!saved.Width.HasValue || Math.Abs(c.Width - saved.Width.Value) < 1e-6)
                && (!saved.Height.HasValue || Math.Abs(c.Height - saved.Height.Value) < 1e-6)
                && c.CoordinateSystem.BasisZ.DistanceTo(Vector(saved.Direction)) < 1e-4).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        internal static string DescribeMismatch(Element element, HistoryPort saved)
        {
            if (element == null) return "élément absent";
            if (saved == null) return "connecteur non enregistré";
            var ports = Ports(element).Where(c => (int)c.Domain == saved.Domain && (int)c.Shape == saved.Shape).ToList();
            if (ports.Count == 0) return "aucun connecteur de même domaine et forme";
            var nearest = ports.OrderBy(c => c.Origin.DistanceTo(Vector(saved.Point))).First();
            double mm = nearest.Origin.DistanceTo(Vector(saved.Point)) * 304.8;
            double degrees = nearest.CoordinateSystem.BasisZ.AngleTo(Vector(saved.Direction)) * 180 / Math.PI;
            string section = saved.Diameter.HasValue ? "; diamètre actuel/ancien="
                + (nearest.Radius * 2 * 304.8).ToString("F3") + "/" + (saved.Diameter.Value * 304.8).ToString("F3") + " mm"
                : saved.Width.HasValue && saved.Height.HasValue ? "; section actuelle/ancienne="
                    + (nearest.Width * 304.8).ToString("F3") + "×" + (nearest.Height * 304.8).ToString("F3") + "/"
                    + (saved.Width.Value * 304.8).ToString("F3") + "×" + (saved.Height.Value * 304.8).ToString("F3") + " mm" : "";
            return "écart=" + mm.ToString("F3") + " mm; orientation=" + degrees.ToString("F3") + "°" + section
                + "; correspondance exacte absente ou ambiguë";
        }
    }
}

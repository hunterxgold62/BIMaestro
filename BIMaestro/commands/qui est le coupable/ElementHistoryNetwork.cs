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
            Diameter = c.Shape == ConnectorProfileType.Round ? (double?)(c.Radius * 2) : null,
            Width = c.Shape != ConnectorProfileType.Round ? (double?)c.Width : null,
            Height = c.Shape != ConnectorProfileType.Round ? (double?)c.Height : null
        };
        internal static List<HistoryConnection> CaptureConnections(Element element)
        {
            var links = new List<HistoryConnection>();
            foreach (var port in Ports(element))
                foreach (Connector peer in port.AllRefs)
                    if (peer.Owner.Id != element.Id && !(peer.Owner is MEPSystem)
                        && (peer.ConnectorType == ConnectorType.End || peer.ConnectorType == ConnectorType.Curve
                            || peer.ConnectorType == ConnectorType.Physical) && port.IsConnectedTo(peer))
                        links.Add(new HistoryConnection { Port = CapturePort(port), Peer = peer.Owner.UniqueId, PeerPort = CapturePort(peer) });
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
            if (kind == null || (points == null && line == null)) return null;
            var start = points == null ? line.GetEndPoint(0) : points.First();
            var end = points == null ? line.GetEndPoint(1) : points.Last();
            var level = curve.ReferenceLevel;
            var type = element.Document.GetElement(element.GetTypeId());
            if (level == null || type == null) return null;
            var systemId = element.get_Parameter(element is Pipe || element is FlexPipe ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM)?.AsElementId();
            var port = Ports(element).OrderBy(c => c.Origin.DistanceTo(start)).FirstOrDefault();
            // Non-round flexible sections need their full twist frame, not just tangents.
            if (points != null && port?.Shape != ConnectorProfileType.Round) return null;
            return new HistoryRecipe
            {
                Kind = "network", Type = type.UniqueId, Level = level.UniqueId, LevelElevation = level.ProjectElevation,
                RestorationOrigins = ElementHistoryRestoration.GetOrigins(element), Connections = CaptureConnections(element),
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
    }
}

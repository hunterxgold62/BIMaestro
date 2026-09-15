using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal sealed class CodexConnectorBuilder
    {
        private readonly List<Tuple<FamilyConnectorSpec, ConnectorElement>> connectors = new List<Tuple<FamilyConnectorSpec, ConnectorElement>>();
        private readonly CodexParametricDesign design;
        internal CodexConnectorBuilder(Document doc, CodexParametricDesign design, IList<Extrusion> parts, CodexParametricBuilder constraints)
        {
            this.design = design;
            foreach (var spec in design.Connectors)
            {
                int index = design.Parts.FindIndex(p => p.Name == spec.Component);
                try
                {
                    using (var geometry = parts[index].get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true }))
                    {
                        var normal = Basis(spec.Axis) * spec.Sign;
                        var face = geometry.OfType<Solid>().SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>().Single(f => f.FaceNormal.DotProduct(normal) > 0.999);
                        ConnectorElement connector;
                        if (spec.Domain == "duct")
                            connector = ConnectorElement.CreateDuctConnector(doc, spec.System == "supply" ? DuctSystemType.SupplyAir : spec.System == "return" ? DuctSystemType.ReturnAir : spec.System == "exhaust" ? DuctSystemType.ExhaustAir : DuctSystemType.OtherAir,
                                spec.Shape == "round" ? ConnectorProfileType.Round : ConnectorProfileType.Rectangular, face.Reference);
                        else if (spec.Domain == "pipe")
                            connector = ConnectorElement.CreatePipeConnector(doc, PipeSystem(spec.System), face.Reference);
                        else connector = ConnectorElement.CreateElectricalConnector(doc, spec.System == "power" ? ElectricalSystemType.PowerCircuit : spec.System == "data" ? ElectricalSystemType.Data : ElectricalSystemType.Communication, face.Reference);
                        if (connector.Direction.DotProduct(normal) < 0) connector.FlipDirection();
                        connector.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.Set(spec.Name);
                        if (spec.Shape == "round") Associate(connector.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS), spec.Diameter, 0.5, constraints);
                        if (spec.Shape == "rectangular")
                        { Associate(connector.get_Parameter(BuiltInParameter.CONNECTOR_WIDTH), spec.Width, 1, constraints); Associate(connector.get_Parameter(BuiltInParameter.CONNECTOR_HEIGHT), spec.Height, 1, constraints); }
                        if (spec.Domain != "electrical")
                        {
                            var flow = connector.get_Parameter(spec.Domain == "duct" ? BuiltInParameter.RBS_DUCT_FLOW_DIRECTION_PARAM : BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM);
                            if (flow == null || flow.IsReadOnly) throw new InvalidOperationException("Paramètre de flux inaccessible.");
                            flow.Set((int)(spec.Flow == "in" ? FlowDirectionType.In : spec.Flow == "out" ? FlowDirectionType.Out : FlowDirectionType.Bidirectional));
                        }
                        connectors.Add(Tuple.Create(spec, connector));
                    }
                }
                catch (Exception ex) { throw new InvalidOperationException("Connecteur « " + spec.Name + " » : " + ex.Message, ex); }
            }
        }
        private static void Associate(Parameter parameter, string name, double factor, CodexParametricBuilder constraints)
        { var expression = new LengthExpression(); expression.Terms.Add(name, factor); constraints.AssociateLength(parameter, expression); }
        private static PipeSystemType PipeSystem(string name) => name == "cold_water" ? PipeSystemType.DomesticColdWater : name == "hot_water" ? PipeSystemType.DomesticHotWater : name == "sanitary" ? PipeSystemType.Sanitary : name == "supply_hydronic" ? PipeSystemType.SupplyHydronic : name == "return_hydronic" ? PipeSystemType.ReturnHydronic : PipeSystemType.OtherPipe;
        private static XYZ Basis(int axis) => axis == 0 ? XYZ.BasisX : axis == 1 ? XYZ.BasisY : XYZ.BasisZ;
        internal void Check(Dictionary<string, double> values)
        {
            foreach (var pair in connectors)
            {
                var spec = pair.Item1; var connector = pair.Item2; var part = design.Parts.Single(p => p.Name == spec.Component);
                var expected = Enumerable.Range(0, 3).Select(a => a == spec.Axis ? (spec.Sign < 0 ? part.Min[a] : part.Max[a]).Value(values) : (part.Min[a].Value(values) + part.Max[a].Value(values)) / 2).ToArray();
                if (connector.Origin.DistanceTo(new XYZ(expected[0], expected[1], expected[2]) / 304.8) * 304.8 > 0.5 || connector.Direction.DotProduct(Basis(spec.Axis) * spec.Sign) < 0.999) throw new InvalidOperationException("Le connecteur ne suit pas sa face : " + spec.Name);
                if (spec.Shape == "round" && Math.Abs(connector.Radius * 609.6 - values[spec.Diameter]) > 0.5 || spec.Shape == "rectangular" && (Math.Abs(connector.Width * 304.8 - values[spec.Width]) > 0.5 || Math.Abs(connector.Height * 304.8 - values[spec.Height]) > 0.5)) throw new InvalidOperationException("Dimensions de connecteur incorrectes : " + spec.Name);
            }
        }
        internal object[] Report() => connectors.Select(p => (object)new { name = p.Item1.Name, domain = p.Item1.Domain, system = p.Item1.System, shape = p.Item1.Shape, element_id = p.Item2.Id.ToString() }).ToArray();
    }
}

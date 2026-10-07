using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexFamilyPipeConnectors
    {
        internal static IList<ConnectorElement> Create(Document doc, Element body, FamilyParameter diameter, XYZ axis, XYZ center)
        {
            if (body == null || !body.Document.Equals(doc) || diameter == null || diameter.Definition.GetDataType() != SpecTypeId.Length)
                throw new InvalidOperationException("Corps local et paramètre DN de longueur requis.");
            if (!diameter.IsInstance) throw new InvalidOperationException("Le DN des connecteurs doit être un paramètre d'occurrence.");
            if (axis == null || axis.GetLength() < 1e-9 || center == null) throw new InvalidOperationException("Axe et centre des raccordements requis.");
            axis = axis.Normalize();
            doc.Regenerate();
            using (var geometry = body.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true }))
            {
                // ComputeNormal(UV.Zero) on a curved face is not a planar-end
                // test. Only actual planar faces may host these end connectors.
                var faces = geometry.OfType<Solid>().Where(s => s.Volume > 1e-12).SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>().ToArray();
                var ends = new[] { -1, 1 }.Select(sign => faces.Where(f => f.FaceNormal.DotProduct(axis) * sign > 0.999)
                    .OrderByDescending(f => f.Origin.DotProduct(axis) * sign).ThenByDescending(f => f.Area).FirstOrDefault()).ToArray();
                if (ends.Any(f => f == null) || (ends[1].Origin - ends[0].Origin).DotProduct(axis) < 1 / 304.8)
                    throw new InvalidOperationException("Deux faces planes opposées et distinctes sont requises sur le corps pour les connecteurs.");
                var result = new List<ConnectorElement>();
                for (int i = 0; i < 2; i++)
                {
                    var connector = ConnectorElement.CreatePipeConnector(doc, PipeSystemType.Global, ends[i].Reference);
                    var parameter = connector.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
                    if (parameter == null || !doc.FamilyManager.CanElementParameterBeAssociated(parameter))
                        throw new InvalidOperationException("Le diamètre du connecteur ne peut pas être associé au DN.");
                    doc.FamilyManager.AssociateElementParameterToFamilyParameter(parameter, diameter);
                    var flow = connector.get_Parameter(BuiltInParameter.RBS_PIPE_FLOW_DIRECTION_PARAM);
                    if (flow != null && !flow.IsReadOnly) flow.Set((int)FlowDirectionType.Bidirectional);
                    doc.Regenerate();
                    XYZ desired = center + axis * ((ends[i].Origin - center).DotProduct(axis));
                    XYZ offset = desired - connector.Origin;
                    if (offset.GetLength() > 1e-7) ElementTransformUtils.MoveElement(doc, connector.Id, offset);
                    doc.Regenerate();
                    XYZ direction = axis * (i == 0 ? -1 : 1);
                    if (connector.Direction.DotProduct(direction) < 0) connector.FlipDirection();
                    if (connector.Origin.DistanceTo(desired) > 0.5 / 304.8 || connector.Direction.DotProduct(direction) < 0.999)
                        throw new InvalidOperationException("Le connecteur n'est pas centré et orienté sur sa face de raccordement.");
                    result.Add(connector);
                }
                return result;
            }
        }
    }
}

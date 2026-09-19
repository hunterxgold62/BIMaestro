using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexFamilyEditor
    {
        private static readonly Guid DrawingSchemaId = new Guid("b76f7d7a-3c9c-4e93-970c-c68c426632f0");
        private static Schema DrawingSchema()
        {
            var schema = Schema.Lookup(DrawingSchemaId);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(DrawingSchemaId);
            builder.SetSchemaName("BIMaestroFamilyDrawingV1");
            builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("Group", typeof(string));
            builder.AddSimpleField("Drawing", typeof(string));
            return builder.Finish();
        }
        private static string Group(Element element)
        {
            var schema = Schema.Lookup(DrawingSchemaId);
            if (schema == null) return null;
            var entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(schema.GetField("Group")) : null;
        }
        internal static void Tag(Element element, string group, string drawing)
        {
            var schema = DrawingSchema(); var entity = new Entity(schema);
            entity.Set(schema.GetField("Group"), group); entity.Set(schema.GetField("Drawing"), drawing);
            element.SetEntity(entity);
        }
        private static void RequireFamily(Document doc, string key = null)
        {
            if (doc == null || !doc.IsFamilyDocument) throw new InvalidOperationException("Ouvrez la famille dans l'éditeur de familles.");
            if (key != null && doc.OwnerFamily.UniqueId != key)
                throw new InvalidOperationException("La famille ne correspond pas à l'inventaire. Relire revit_inspect_family.");
        }
        internal static object Inspect(Document doc, JObject args)
        {
            RequireFamily(doc); CodexFamilyDesign.Keys(args, "offset", "limit");
            double offset = CodexFamilyDesign.Scalar(args["offset"], "offset", 0, 1000000);
            double limit = CodexFamilyDesign.Scalar(args["limit"], "limit", 1, 100);
            if (offset != Math.Truncate(offset) || limit != Math.Truncate(limit)) throw new InvalidOperationException("Pagination entière attendue.");
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
                .Where(e => e is GenericForm || e is CurveElement || e is FamilyInstance || e is ReferencePlane || e is SketchPlane || e is Dimension || e is ConnectorElement || e is Opening)
                .OrderBy(e => e.Id.ToString(), StringComparer.Ordinal).ToArray();
            return new { document_key = doc.OwnerFamily.UniqueId, document = doc.Title, saved_path = doc.PathName,
                parameters = CodexFamilyTools.Read(doc), total = elements.Length, offset = (int)offset,
                materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => new { unique_id = m.UniqueId, name = m.Name }).ToArray(),
                next_offset = offset + limit < elements.Length ? (int?)(offset + limit) : null,
                drawing_groups = elements.Where(e => Group(e) != null).GroupBy(Group).Select(g => new { name = g.Key, count = g.Count() }).ToArray(),
                elements = elements.Skip((int)offset).Take((int)limit).Select(e => Describe(doc, e)).ToArray(),
                note = "État réel du type courant, coordonnées internes en mm. Inventaire paginé, pas une extraction complète des contraintes ni une recette de reconstruction. Les profils longs sont tronqués explicitement." };
        }
        private static double[] Mm(XYZ p) => new[] { p.X * 304.8, p.Y * 304.8, p.Z * 304.8 };
        private static object CurveInfo(Curve curve) => new { kind = curve.GetType().Name,
            start_mm = curve.IsBound ? Mm(curve.GetEndPoint(0)) : null,
            end_mm = curve.IsBound ? Mm(curve.GetEndPoint(1)) : null,
            midpoint_mm = curve.IsBound ? Mm(curve.Evaluate(0.5, true)) : null };
        private static object ParameterInfo(Document doc, Parameter p)
        {
            if (p == null) return null;
            var associated = doc.FamilyManager.GetAssociatedFamilyParameter(p);
            return new { value_mm = p.AsDouble() * 304.8, associated_parameter = associated?.Definition.Name,
                editable_directly = !p.IsReadOnly && associated == null };
        }
        private static object Describe(Document doc, Element element)
        {
            var result = new JObject { ["unique_id"] = element.UniqueId, ["id"] = element.Id.ToString(),
                ["name"] = element.Name, ["kind"] = element.GetType().Name, ["category"] = element.Category?.Name,
                ["pinned"] = element.Pinned, ["drawing_group"] = Group(element) };
            var box = element.get_BoundingBox(null);
            if (box != null)
            {
                var corners = Enumerable.Range(0, 8).Select(i => box.Transform.OfPoint(new XYZ(
                    (i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z))).ToArray();
                result["bounds_mm"] = JObject.FromObject(new { min = Mm(new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z))),
                    max = Mm(new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z))) });
            }
            if (element is CurveElement curve) result["curve"] = JObject.FromObject(CurveInfo(curve.GeometryCurve));
            var visible = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (visible != null)
            {
                bool associable = doc.FamilyManager.CanElementParameterBeAssociated(visible);
                result["visibility"] = JObject.FromObject(new { property = "visibility", parameter_id = visible.Id.ToString(),
                    associable, value = visible.AsInteger() != 0,
                    associated_parameter = associable ? doc.FamilyManager.GetAssociatedFamilyParameter(visible)?.Definition.Name : null });
            }
            if (element is ReferencePlane reference)
                result["reference_plane"] = JObject.FromObject(new { bubble_mm = Mm(reference.BubbleEnd), free_mm = Mm(reference.FreeEnd), normal = Mm(reference.GetPlane().Normal / 304.8) });
            if (element is GenericForm form)
            {
                var material = form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                using (var visibility = form.GetVisibility())
                    result["form"] = JObject.FromObject(new { solid = form.IsSolid,
                        material_name = material == null ? null : doc.GetElement(material.AsElementId())?.Name,
                        material_parameter = material == null ? null : doc.FamilyManager.GetAssociatedFamilyParameter(material)?.Definition.Name,
                        coarse = visibility.IsShownInCoarse, medium = visibility.IsShownInMedium, fine = visibility.IsShownInFine,
                        top_bottom = visibility.IsShownInTopBottom, front_back = visibility.IsShownInFrontBack, left_right = visibility.IsShownInLeftRight });
            }
            if (element is Extrusion extrusion)
            {
                var curves = extrusion.Sketch.Profile.Cast<CurveArray>().SelectMany(a => a.Cast<Curve>()).ToArray();
                result["extrusion"] = JObject.FromObject(new {
                    solid = extrusion.IsSolid,
                    start = ParameterInfo(doc, extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM)),
                    end = ParameterInfo(doc, extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM)),
                    direction = Mm(extrusion.Sketch.SketchPlane.GetPlane().Normal / 304.8),
                    profile_curve_count = curves.Length, profile_truncated = curves.Length > 64,
                    profile = curves.Take(64).Select(CurveInfo).ToArray() });
            }
            return result;
        }
        internal static object EditRepresentation(Document doc, JObject args, Func<string, string, bool> confirm,
            Func<string, Transaction> transactionFactory, Action<Transaction> commit)
        {
            var request = FamilyRepresentationEdit.Parse(args); RequireFamily(doc, request.DocumentKey);
            var previous = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
                .Where(e => string.Equals(Group(e), request.Group, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (request.Action == "add" && previous.Length != 0) throw new InvalidOperationException("Ce groupe de dessins existe déjà ; utiliser replace pour le modifier.");
            if (request.Action != "add" && previous.Length == 0) throw new InvalidOperationException("Groupe de dessins absent. Les dessins manuels ne sont pas supprimés par cet outil.");
            if (previous.Any(e => e.Pinned || e.GroupId != ElementId.InvalidElementId)) throw new InvalidOperationException("Un dessin ciblé est verrouillé ou appartient à un groupe Revit.");
            if (!confirm("Modifier la représentation — " + request.Group,
                request.Action + " : " + previous.Length + " éléments existants ciblés ; " + (request.Representation?.Drawings.Count ?? 0) + " dessins demandés. La famille reste ouverte ; un Ctrl+Z annule le lot."))
                throw new InvalidOperationException("Modification refusée. Ne pas réessayer sans nouvelle demande.");
            object report = null;
            using (var group = new TransactionGroup(doc, "Codex — représentation de famille"))
            {
                group.Start();
                // Includes nested region loading: any later failure rolls all of it back.
                var regions = CodexRepresentationBuilder.PrepareRegions(doc, request.Representation);
                using (var transaction = transactionFactory("Codex — dessins de famille"))
                {
                    if (previous.Length != 0)
                    {
                        var permitted = new HashSet<ElementId>(previous.Select(e => e.Id));
                        var deleted = doc.Delete(permitted.ToList());
                        if (deleted.Any(id => !permitted.Contains(id)))
                            throw new InvalidOperationException("La suppression toucherait des contraintes ou éléments dépendants. Opération annulée pour conserver l'existant.");
                    }
                    report = CodexRepresentationBuilder.Build(doc, request.Representation, Array.Empty<Element>(), regions,
                        (element, drawing) => Tag(element, request.Group, drawing));
                    doc.Regenerate(); commit(transaction);
                }
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Modification 2D annulée.");
            }
            return new { document_key = doc.OwnerFamily.UniqueId, group_name = request.Group, action = request.Action,
                removed_count = previous.Length, representation = report, saved = false, undo = "Ctrl+Z",
                note = "Même famille, sans reconstruction du modèle. Dessins fixes : ils ne suivent pas automatiquement les paramètres de dimensions. construction.json n'est pas actualisé ; relire l'inventaire réel." };
        }
        internal static object EditExtrusion(Document doc, JObject args, Func<string, string, bool> confirm,
            Func<string, Transaction> transactionFactory, Action<Transaction> commit)
        {
            var request = FamilyExtrusionEdit.Parse(args); RequireFamily(doc, request.DocumentKey);
            var extrusion = doc.GetElement(request.UniqueId) as Extrusion;
            if (extrusion == null) throw new InvalidOperationException("Extrusion introuvable ; relire l'inventaire.");
            if (extrusion.Pinned || extrusion.GroupId != ElementId.InvalidElementId) throw new InvalidOperationException("Extrusion verrouillée ou dans un groupe Revit.");
            var start = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            foreach (var parameter in new[] { start, end })
            {
                if (parameter == null || parameter.IsReadOnly) throw new InvalidOperationException("Limite d'extrusion non modifiable.");
                var associated = doc.FamilyManager.GetAssociatedFamilyParameter(parameter);
                if (associated != null) throw new InvalidOperationException("Modifier le paramètre associé « " + associated.Definition.Name + " » avec revit_set_family_parameters ; son association doit être conservée.");
            }
            var before = new { start_mm = start.AsDouble() * 304.8, end_mm = end.AsDouble() * 304.8 };
            if (!confirm("Modifier la profondeur de l'extrusion " + extrusion.Id,
                "Début " + before.start_mm + " → " + request.Start + " mm ; fin " + before.end_mm + " → " + request.End + " mm. Coordonnées selon l'axe du profil ; un Ctrl+Z annule."))
                throw new InvalidOperationException("Modification refusée. Ne pas réessayer sans nouvelle demande.");
            using (var group = new TransactionGroup(doc, "Codex — profondeur d'extrusion"))
            {
                group.Start();
                using (var transaction = transactionFactory("Codex — limites d'extrusion"))
                {
                    // Set the outward endpoint first to avoid a transient inverted extrusion.
                    if (request.Start >= before.end_mm) { end.Set(request.End / 304.8); start.Set(request.Start / 304.8); }
                    else { start.Set(request.Start / 304.8); end.Set(request.End / 304.8); }
                    doc.Regenerate(); commit(transaction);
                }
                if (Math.Abs(start.AsDouble() * 304.8 - request.Start) > 0.001 || Math.Abs(end.AsDouble() * 304.8 - request.End) > 0.001)
                    throw new InvalidOperationException("Les contraintes empêchent les limites demandées ; modification annulée.");
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Modification 3D annulée.");
            }
            return new { saved = false, undo = "Ctrl+Z", before, element = Describe(doc, extrusion) };
        }
    }
}

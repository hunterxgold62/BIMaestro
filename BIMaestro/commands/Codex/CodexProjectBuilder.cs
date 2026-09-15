using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMaestro.Codex
{
    internal static class CodexProjectBuilder
    {
        internal static object Create(Document doc, CodexFamilyDesign design, double[] originMm, double rotation)
        {
            var warnings = new List<string>();
            var ids = new List<ElementId>();
            var bounds = new CodexFamilyBuilder.Bounds();
            var origin = new XYZ(originMm[0] / 304.8, originMm[1] / 304.8, originMm[2] / 304.8);
            var category = new ElementId(CodexFamilyBuilder.CategoryId(design.Category));
            if (!DirectShape.IsValidCategoryId(category, doc)) throw new InvalidOperationException("Catégorie non prise en charge pour des volumes libres.");
            using (var transaction = Begin(doc, "Codex — composition libre", warnings))
            {
                var materials = new Dictionary<string, ElementId>();
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                foreach (var spec in design.Materials)
                {
                    var material = (Material)doc.GetElement(Material.Create(doc, "BIMaestro " + spec.Name + " " + suffix));
                    material.Color = new Color(spec.Rgb[0], spec.Rgb[1], spec.Rgb[2]); material.Transparency = spec.Transparency;
                    materials.Add(spec.Name, material.Id);
                }
                foreach (var part in design.Parts)
                {
                    Solid solid = null;
                    try
                    {
                        solid = CodexFamilyBuilder.MakeSolid(part.Geometry, materials[part.Material]);
                        foreach (var cut in part.Cuts)
                            using (var cutter = CodexFamilyBuilder.MakeSolid(cut, materials[part.Material]))
                            {
                                double before = solid.Volume;
                                var difference = BooleanOperationsUtils.ExecuteBooleanOperation(solid, cutter, BooleanOperationsType.Difference);
                                solid.Dispose(); solid = difference;
                                if (Math.Abs(before - solid.Volume) < 1e-10) warnings.Add("Évidement sans intersection : " + part.Name);
                            }
                        for (int i = 0; i < part.Count; i++)
                            using (var copy = SolidUtils.CreateTransformed(solid, Transform.CreateTranslation(new XYZ(part.Step[0], part.Step[1], part.Step[2]) * (i / 304.8))))
                                ids.Add(Add(doc, category, copy, design.Name + " — " + part.Name));
                    }
                    catch (Exception ex) { throw new InvalidOperationException("Pièce « " + part.Name + " » : " + ex.Message, ex); }
                    finally { solid?.Dispose(); }
                }
                doc.Regenerate();
                foreach (var id in ids) bounds.Include(doc.GetElement(id).get_BoundingBox(null));
                design.CheckDimensions(new[] { (bounds.Max.X - bounds.Min.X) * 304.8, (bounds.Max.Y - bounds.Min.Y) * 304.8, (bounds.Max.Z - bounds.Min.Z) * 304.8 });
                if (rotation != 0)
                    using (var axis = Line.CreateUnbound(XYZ.Zero, XYZ.BasisZ)) ElementTransformUtils.RotateElements(doc, ids, axis, rotation * Math.PI / 180);
                if (!origin.IsAlmostEqualTo(XYZ.Zero)) ElementTransformUtils.MoveElements(doc, ids, origin);
                Finish(transaction, warnings);
            }
            return new { created_element_ids = ids.Select(id => id.ToString()).ToArray(), kind = "DirectShape", family = false, saved = false,
                material_count = design.Materials.Count, project_origin_mm = originMm, project_rotation_deg = rotation, warnings,
                undo = "Un Ctrl+Z annule la composition et ses nouveaux matériaux." };
        }

        internal static object Barrier(Document doc, IList<Curve> curves, double heightMm, double widthMm, double offsetMm)
        {
            var warnings = new List<string>();
            var ids = new List<string>();
            using (var transaction = Begin(doc, "Codex — muraille en volumes indépendants", warnings))
            {
                for (int i = 0; i < curves.Count; i++)
                {
                    try
                    {
                        using (var a = curves[i].CreateOffset(widthMm / 609.6, XYZ.BasisZ))
                        using (var b = curves[i].CreateOffset(-widthMm / 609.6, XYZ.BasisZ))
                        using (var loop = new CurveLoop())
                        {
                            loop.Append(a); loop.Append(Line.CreateBound(a.GetEndPoint(1), b.GetEndPoint(1)));
                            loop.Append(b.CreateReversed()); loop.Append(Line.CreateBound(b.GetEndPoint(0), a.GetEndPoint(0)));
                            using (var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, heightMm / 304.8))
                            using (var placed = SolidUtils.CreateTransformed(solid, Transform.CreateTranslation(new XYZ(0, 0, offsetMm / 304.8))))
                                ids.Add(Add(doc, new ElementId(BuiltInCategory.OST_GenericModel), placed, "Muraille — segment " + (i + 1)).ToString());
                        }
                    }
                    catch (Exception ex) { throw new InvalidOperationException("Muraille, arête " + i + " : " + ex.Message, ex); }
                }
                Finish(transaction, warnings);
            }
            return new { created_element_ids = ids, kind = "DirectShape / Modèles génériques", native_walls = false, height_mm = heightMm,
                width_mm = widthMm, saved = false, warnings, note = "Volumes indépendants sans jonctions automatiques ni délimitation de pièces. Extrémités droites ; angles non fusionnés.", undo = "Un Ctrl+Z annule le lot." };
        }

        private static ElementId Add(Document doc, ElementId category, Solid solid, string name)
        {
            if (solid.Faces.Size == 0 || solid.Volume <= 1e-12) throw new InvalidOperationException("Solide vide après évidement.");
            var shape = DirectShape.CreateElement(doc, category);
            shape.ApplicationId = "BIMaestro.Codex"; shape.ApplicationDataId = Guid.NewGuid().ToString("N");
            shape.SetShape(new List<GeometryObject> { solid });
            shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(name);
            return shape.Id;
        }
        private static Transaction Begin(Document doc, string name, List<string> warnings)
        {
            var transaction = new Transaction(doc, name); transaction.Start();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new CodexFamilyBuilder.Failures(warnings)).SetClearAfterRollback(true));
            return transaction;
        }
        private static void Finish(Transaction transaction, List<string> warnings)
        {
            if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Création annulée : " + string.Join(" ; ", warnings));
        }
    }
}
